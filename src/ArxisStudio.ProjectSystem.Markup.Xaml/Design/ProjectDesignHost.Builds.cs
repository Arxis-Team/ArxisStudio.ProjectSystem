using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>The designer's own builds: after a quiet moment, of the top projects of what changed, one at a time.</summary>
public sealed partial class ProjectDesignHost
{
    private readonly ITimer _buildTimer;
    private readonly HashSet<ProjectIdentity> _pendingBuild = [];
    private readonly HashSet<ProjectIdentity> _restorePending = [];

    private string? _pendingReason;
    private bool _buildArmed;
    private bool _buildLoopRunning;
    private Task? _buildLoop;

    /// <summary>Builds the design set, or the projects named and what references them, now.</summary>
    /// <remarks>
    /// <para>
    /// The build is the designer's own, with <see cref="ProjectDesignHostOptions.BuildProperties"/>, so
    /// it never writes over the IDE's. It builds the <em>top</em> projects of what is affected: a
    /// library's change is built by building the application that references it, which builds the
    /// library and recompiles what uses it — building the library alone would leave the application's
    /// code compiled against members that may be gone. A restore runs first when a project of it was
    /// never restored, or something its restore reads changed.
    /// </para>
    /// <para>
    /// Builds take turns, and this one takes over what was waiting for the quiet moment. When it
    /// rewrote what the live generation was loaded from, the generation is to be replaced
    /// (<see cref="ProjectDesignBuildResult.TypesChanged"/>), as soon as nothing holds the swap off.
    /// </para>
    /// </remarks>
    /// <param name="reason">Why, for whoever reads the result.</param>
    /// <param name="projects">The projects whose code changed, or empty for the whole design set.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What the build did. Nothing is built for projects outside the design set.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/>, empty or blank.</exception>
    /// <exception cref="InvalidOperationException">The workspace has published nothing.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async ValueTask<ProjectDesignBuildResult> BuildAsync(
        string reason,
        ImmutableArray<ProjectIdentity> projects = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ThrowIfDisposed();

        ProjectDesignBuildResult result;

        await _building.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ReportStateAsync().ConfigureAwait(false);

            SolutionSnapshot snapshot = _source.Snapshot
                ?? throw new InvalidOperationException("Load the workspace before building: a build is of a snapshot's projects.");

            ImmutableArray<ProjectIdentity> set = DesignSetOf(snapshot);
            ImmutableArray<ProjectIdentity> changed = projects.IsDefaultOrEmpty ? set : projects;

            TakeOver(projects.IsDefaultOrEmpty ? null : changed);

            result = await BuildCoreAsync(snapshot, changed, set, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _building.Release();
        }

        await ReportStateAsync().ConfigureAwait(false);

        TrySwapSoon();

        return result;
    }

    /// <summary>Takes waiting work over: all of it, or what is named.</summary>
    private void TakeOver(ImmutableArray<ProjectIdentity>? projects)
    {
        bool disarm;

        lock (_sync)
        {
            if (projects is { } named)
            {
                _pendingBuild.ExceptWith(named);
            }
            else
            {
                _pendingBuild.Clear();
            }

            disarm = _pendingBuild.Count == 0 && _buildArmed;

            if (disarm)
            {
                _buildArmed = false;
            }
        }

        if (disarm)
        {
            _buildTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Asks for a build of projects once the code has been quiet for the build delay.</summary>
    /// <remarks>Every request starts the quiet moment again: an editor's save-all is one build, not one per file.</remarks>
    private void ScheduleBuild(ImmutableArray<ProjectIdentity> projects, string reason)
    {
        lock (_sync)
        {
            // Nothing is shown of a design set built against another Avalonia, so nothing is built for it.
            if (Volatile.Read(ref _disposed) != 0 || _unsupportedReason is not null)
            {
                return;
            }

            _pendingBuild.UnionWith(projects);
            _pendingReason = reason;
            _buildArmed = true;
        }

        // Outside the lock: a clock may call back on this very thread.
        _buildTimer.Change(_options.BuildDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The builds the host started on its own, or a completed task when none are running. For tests.</summary>
    internal Task BuildInFlight
    {
        get
        {
            lock (_sync)
            {
                return _buildLoop ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>The quiet moment is over: start building, unless a build in flight takes the work after itself.</summary>
    private void OnBuildDue()
    {
        lock (_sync)
        {
            _buildArmed = false;

            if (_buildLoopRunning || _pendingBuild.Count == 0 || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _buildLoopRunning = true;
            _buildLoop = Task.Run(() => RunBuildsAsync(_shutdown.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Builds what is waiting, one build at a time, until nothing waits or a new quiet moment has started.
    /// </summary>
    private async Task RunBuildsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ImmutableArray<ProjectIdentity> projects;
            string reason;

            lock (_sync)
            {
                if (_pendingBuild.Count == 0 || _buildArmed || Volatile.Read(ref _disposed) != 0)
                {
                    _buildLoopRunning = false;

                    break;
                }

                projects = [.. _pendingBuild];
                reason = _pendingReason ?? "the code changed";

                _pendingBuild.Clear();
            }

            try
            {
                await _building.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await ReportStateAsync().ConfigureAwait(false);

                    if (_source.Snapshot is { } snapshot)
                    {
                        await BuildCoreAsync(snapshot, projects, DesignSetOf(snapshot), reason, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _building.Release();
                }

                await ReportStateAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _buildLoopRunning = false;
                }

                return;
            }
            catch (Exception exception)
            {
                await ReportFailureAsync("building the project", exception).ConfigureAwait(false);
            }
        }

        TrySwapSoon();
    }

    /// <summary>
    /// Builds the projects of the design set that are out of date, before a generation is made of
    /// them. The caller holds <see cref="_building"/>.
    /// </summary>
    private async Task BuildWhatIsOutOfDateAsync(string reason, CancellationToken cancellationToken)
    {
        if (_source.Snapshot is not { } snapshot)
        {
            return;
        }

        ImmutableArray<ProjectIdentity> set = DesignSetOf(snapshot);
        ImmutableArray<ProjectIdentity> due = OutOfDate(snapshot, set);

        if (!due.IsEmpty)
        {
            await BuildCoreAsync(snapshot, due, set, reason, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores when it has to, builds the top projects of what changed, and says whether the types
    /// moved. The caller holds <see cref="_building"/>.
    /// </summary>
    private async Task<ProjectDesignBuildResult> BuildCoreAsync(
        SolutionSnapshot snapshot,
        ImmutableArray<ProjectIdentity> changed,
        ImmutableArray<ProjectIdentity> set,
        string reason,
        CancellationToken cancellationToken)
    {
        (ImmutableArray<ProjectIdentity> tops, ImmutableArray<ProjectIdentity> affected) = TopsOf(snapshot, changed, set);

        if (tops.IsEmpty)
        {
            return new ProjectDesignBuildResult(ProjectOperationStatus.Succeeded, [], [], reason, restored: false, typesChanged: false, TimeSpan.Zero);
        }

        long started = _options.TimeProvider.GetTimestamp();
        ImmutableArray<ProjectDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<ProjectDiagnostic>();
        ProjectOperationStatus status = ProjectOperationStatus.Succeeded;
        bool restored = false;

        if (NeedsRestore(snapshot, affected))
        {
            ProjectOperationResult restore = await ExecuteAsync(snapshot, ProjectOperationKind.Restore, tops, cancellationToken).ConfigureAwait(false);

            diagnostics.AddRange(restore.Diagnostics);
            status = restore.Status;
            restored = true;

            if (status == ProjectOperationStatus.Succeeded)
            {
                lock (_sync)
                {
                    _restorePending.ExceptWith(affected);
                }

                // The model reads what restore wrote: packages, and the paths of their assemblies.
                await _source.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (status == ProjectOperationStatus.Succeeded)
        {
            ProjectOperationResult build = await ExecuteAsync(snapshot, ProjectOperationKind.Build, tops, cancellationToken).ConfigureAwait(false);

            diagnostics.AddRange(build.Diagnostics);
            status = build.Status;
        }

        bool typesChanged = status == ProjectOperationStatus.Succeeded && GenerationMovedOnDisk();

        if (typesChanged)
        {
            MarkStale("the project's code was built");
        }

        var result = new ProjectDesignBuildResult(
            status, tops, diagnostics.ToImmutable(), reason, restored, typesChanged, _options.TimeProvider.GetElapsedTime(started));

        await RaiseAsync(BuildCompleted, new ProjectDesignBuildCompletedEventArgs(result)).ConfigureAwait(false);

        return result;
    }

    /// <summary>Whether a build rewrote what the live generation was loaded from.</summary>
    private bool GenerationMovedOnDisk()
    {
        ProjectAssemblyContext? generation;

        lock (_sync)
        {
            generation = _generation;
        }

        return generation is { IsUnloaded: false } && !generation.IsCurrentOnDisk();
    }

    /// <summary>Runs one restore or build of the top projects, as the workspace was loaded and with the build properties.</summary>
    private Task<ProjectOperationResult> ExecuteAsync(
        SolutionSnapshot snapshot,
        ProjectOperationKind kind,
        ImmutableArray<ProjectIdentity> projects,
        CancellationToken cancellationToken)
    {
        // The request the snapshot was evaluated with: the source publishes the two together.
        WorkspaceLoadRequest request = snapshot.Request;

        var operation = new ProjectOperationRequest
        {
            Kind = kind,
            Workspace = snapshot.Workspace,
            EntryPointPath = request.EntryPointPath,
            Projects = projects,
            Configuration = _options.Configuration ?? request.Configuration,
            Platform = request.Platform,
            TargetFramework = request.TargetFramework,
            GlobalProperties = ProjectMetadata.Create(request.GlobalProperties.Concat(_options.BuildProperties)),
        };

        return _source.ExecuteAsync(operation, progress: null, cancellationToken).AsTask();
    }

    /// <summary>
    /// The top projects of what a change affects — those of the design set that changed or reference
    /// one that did, less those another affected project builds — and the affected ones.
    /// </summary>
    internal static (ImmutableArray<ProjectIdentity> Tops, ImmutableArray<ProjectIdentity> Affected) TopsOf(
        SolutionSnapshot snapshot,
        IEnumerable<ProjectIdentity> changed,
        ImmutableArray<ProjectIdentity> set)
    {
        var inSet = new HashSet<ProjectIdentity>(set);
        var origins = new HashSet<ProjectIdentity>(changed.Where(inSet.Contains));

        if (origins.Count == 0)
        {
            return ([], []);
        }

        var reach = new Dictionary<ProjectIdentity, HashSet<ProjectIdentity>>();

        ImmutableArray<ProjectIdentity> affected =
            [.. set.Where(project => origins.Contains(project) || Reach(project).Overlaps(origins))];

        ImmutableArray<ProjectIdentity> tops =
            [.. affected.Where(project => !affected.Any(other => other != project && Reach(other).Contains(project)))];

        return (tops, affected);

        // Every project a project builds through its references, however deep.
        HashSet<ProjectIdentity> Reach(ProjectIdentity project)
        {
            if (reach.TryGetValue(project, out HashSet<ProjectIdentity>? known))
            {
                return known;
            }

            var found = new HashSet<ProjectIdentity>();
            var pending = new Stack<ProjectIdentity>([project]);

            reach[project] = found;

            while (pending.Count > 0)
            {
                if (!snapshot.TryGetProject(pending.Pop(), out ProjectSnapshot? current))
                {
                    continue;
                }

                foreach (ProjectReferenceInfo reference in current.ProjectReferences)
                {
                    if (!reference.Project.IsEmpty && reference.Project != project && found.Add(reference.Project))
                    {
                        pending.Push(reference.Project);
                    }
                }
            }

            return found;
        }
    }

    /// <summary>Whether a restore has to run before the projects build.</summary>
    /// <remarks>
    /// A project that was never restored — none of what a restore writes is on disk — or one something
    /// its restore reads changed for since. A project whose provider names no restore output has no
    /// restore to run.
    /// </remarks>
    private bool NeedsRestore(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> projects)
    {
        foreach (ProjectIdentity identity in projects)
        {
            lock (_sync)
            {
                if (_restorePending.Contains(identity))
                {
                    return true;
                }
            }

            if (snapshot.TryGetProject(identity, out ProjectSnapshot? project)
                && !project.RestoreOutputs.IsEmpty
                && !project.RestoreOutputs.Any(static output => File.Exists(output.Value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The projects of a set whose output is missing, or older than something it is built from: its
    /// code, its markup, or an input of its evaluation.
    /// </summary>
    /// <remarks>
    /// Asked of the files' times rather than of the assembly: the assembly is what a generation is about
    /// to load, and loading it to find out whether it is worth loading is a circle. A build that finds
    /// nothing to do costs an evaluation; one skipped wrongly shows yesterday's types.
    /// </remarks>
    internal static ImmutableArray<ProjectIdentity> OutOfDate(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        ImmutableArray<ProjectIdentity>.Builder due = ImmutableArray.CreateBuilder<ProjectIdentity>();

        foreach (ProjectIdentity identity in set)
        {
            if (!snapshot.TryGetProject(identity, out ProjectSnapshot? project)
                || project.Outputs.FirstOrDefault(static output => output.Kind == OutputArtifactKind.Assembly) is not { } output)
            {
                continue;
            }

            DateTime built = WrittenAt(output.Path);

            if (built == DateTime.MinValue
                || project.EvaluationInputs.Any(input => WrittenAt(input) > built)
                || project.Items.Any(item => (IsCode(item) || IsMarkupItem(item)) && !item.FullPath.IsEmpty && WrittenAt(item.FullPath) > built))
            {
                due.Add(identity);
            }
        }

        return due.ToImmutable();

        static DateTime WrittenAt(CanonicalPath path)
        {
            try
            {
                return File.Exists(path.Value) ? File.GetLastWriteTimeUtc(path.Value) : DateTime.MinValue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DateTime.MinValue;
            }
        }
    }
}
