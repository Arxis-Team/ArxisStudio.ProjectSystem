using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Keeps a designer's documents showing a project's types as they are on disk, beside an IDE that
/// edits and builds the same project.
/// </summary>
/// <remarks>
/// <para>
/// The orchestration a designer needs and nobody should write twice: one generation of the design
/// set's types (<see cref="ProjectAssemblyContext.Create(SolutionSnapshot, IEnumerable{ProjectIdentity}, string?)"/>),
/// an environment per project, the live documents open against it, the population that makes placed
/// controls follow their documents, the designer's own builds, and — when a build rewrote the types —
/// the swap: everything let go, the generation proven gone, and the successor shown in its place
/// (<c>docs/adr/0028-the-design-host-replaces-a-generation-in-order.md</c>).
/// </para>
/// <para>
/// <b>The host does not watch.</b> Its owner feeds it file changes (<see cref="NotifyChanged"/>), as
/// ADR 0016 has every host compose watching, and it classifies them against the current snapshot: a
/// saved form is read again, saved code is built after a quiet moment, a file appearing re-reads the
/// project, a renamed form follows its file.
/// </para>
/// <para>
/// <b>A swap waits only for the designer.</b> Whatever is in the middle of something holds it off
/// through <see cref="Gate"/>; the swap runs the moment the last deferral lets go and no build is
/// pending. The window being in the background, or the application running from the designer, does
/// not hold it off. When the generation will not go, there is no successor:
/// <see cref="RestartRequired"/> says only a new process shows the types as they are now.
/// </para>
/// <para>
/// <b>Threads.</b> Call it from the user interface thread. Its events are raised there, and so are
/// the participants' calls; the work between them — builds, reclaims, reading files — runs off it.
/// </para>
/// </remarks>
public sealed partial class ProjectDesignHost : IAsyncDisposable
{
    private readonly ProjectWorkspace _workspace;
    private readonly ProjectDesignHostOptions _options;
    private readonly Lock _sync = new();

    // Generation-level work takes turns: starting, swapping, and attaching documents. Opening a
    // document during a swap waits here for the successor. Taken before _building, never after.
    private readonly SemaphoreSlim _turn = new(1, 1);

    // One build at a time, and none while a generation is created: a generation is one build of
    // everything, which it cannot be while a build is writing the files it loads.
    private readonly SemaphoreSlim _building = new(1, 1);

    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<IProjectDesignParticipant> _participants = [];
    private readonly Dictionary<ProjectIdentity, XamlLoadEnvironment> _environments = [];

    private Task? _pump;
    private ProjectAssemblyContext? _generation;
    private ProjectXamlPopulation? _population;
    private XamlMemberResolver? _members;
    private ProjectResourceMap? _resources;
    private ImmutableArray<ProjectIdentity> _designSet = [];
    private ProjectDesignState _reported = ProjectDesignState.Starting;
    private bool _started;
    private bool _swapping;
    private ProjectDesignRestartReason? _restartReason;
    private bool _stale;
    private string? _staleReason;
    private int _born;
    private int _disposed;

    /// <summary>Creates a host over a workspace the owner has loaded.</summary>
    /// <param name="workspace">The workspace whose snapshot says what the project is.</param>
    /// <param name="options">How to build, wait and let go, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is <see langword="null"/>.</exception>
    public ProjectDesignHost(ProjectWorkspace workspace, ProjectDesignHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _options = options ?? ProjectDesignHostOptions.Default;

        Gate = new ProjectDesignSwapGate();
        Gate.Changed += OnGateChanged;

        _buildTimer = _options.TimeProvider.CreateTimer(
            static state => ((ProjectDesignHost)state!).OnBuildDue(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _workspace.SnapshotChanged += OnSnapshotChanged;
    }

    /// <summary>Raised on the user interface thread when <see cref="State"/> moves.</summary>
    public event EventHandler<ProjectDesignStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on the user interface thread when a design build finished.</summary>
    public event EventHandler<ProjectDesignBuildCompletedEventArgs>? BuildCompleted;

    /// <summary>Raised on the user interface thread when a generation was replaced, or found held.</summary>
    public event EventHandler<ProjectDesignSwapCompletedEventArgs>? SwapCompleted;

    /// <summary>Raised on the user interface thread when only a new process can show the types as they are now.</summary>
    public event EventHandler<ProjectDesignRestartEventArgs>? RestartRequired;

    /// <summary>Raised on the user interface thread when a document's file changed under its unsaved edits.</summary>
    public event EventHandler<ProjectDesignConflictEventArgs>? ExternalConflict;

    /// <summary>Raised on the user interface thread when an open document followed its file to a new place.</summary>
    public event EventHandler<ProjectDesignDocumentEventArgs>? DocumentMoved;

    /// <summary>
    /// Raised on the user interface thread when an open document's file, or a folder it was in, was
    /// deleted. The document stays open until its owner closes it (<see cref="CloseDocumentAsync"/>).
    /// </summary>
    public event EventHandler<ProjectDesignDocumentEventArgs>? DocumentDeleted;

    /// <summary>Raised on the user interface thread once a batch of file changes has been dealt with.</summary>
    public event EventHandler<ProjectDesignChangesEventArgs>? ChangesApplied;

    /// <summary>
    /// Raised on the user interface thread when an instance of a project's control was drawn from its
    /// compiled markup because its live document could not do it.
    /// </summary>
    public event EventHandler<XamlLivePopulationFailedEventArgs>? PopulationFailed;

    /// <summary>Raised on the user interface thread when work the host ran on its own failed unexpectedly.</summary>
    public event EventHandler<ProjectDesignFailureEventArgs>? OperationFailed;

    /// <summary>Gets what holds a swap off.</summary>
    public ProjectDesignSwapGate Gate { get; }

    /// <summary>Gets where the host is in its life.</summary>
    public ProjectDesignState State
    {
        get
        {
            lock (_sync)
            {
                return ComputeState();
            }
        }
    }

    /// <summary>Gets the projects of the current generation — those with documents, and what they reference.</summary>
    public ImmutableArray<ProjectIdentity> DesignSet
    {
        get
        {
            lock (_sync)
            {
                return _designSet;
            }
        }
    }

    /// <summary>Gets the name of the live generation, or <see langword="null"/> when there is none.</summary>
    /// <remarks>A name rather than the generation: a designer that held the generation could not let it go.</remarks>
    public string? GenerationName
    {
        get
        {
            lock (_sync)
            {
                return _generation?.Name;
            }
        }
    }

    /// <summary>Gets what the live generation could not load as the projects asked.</summary>
    public ImmutableArray<ProjectDiagnostic> GenerationDiagnostics
    {
        get
        {
            lock (_sync)
            {
                return _generation?.Diagnostics ?? [];
            }
        }
    }

    /// <summary>Gets why the generation is to be replaced, while it is.</summary>
    public string? StaleReason
    {
        get
        {
            lock (_sync)
            {
                return _stale ? _staleReason : null;
            }
        }
    }

    /// <summary>
    /// Builds what is out of date, creates the first generation, and starts dealing with changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workspace must be loaded with the host's <see cref="ProjectDesignHostOptions.BuildProperties"/>
    /// among its global properties, so that the snapshot names the outputs the host's builds write: a
    /// generation of the IDE's outputs would never see a design build, and the designer would show the
    /// types the IDE last built for as long as it ran.
    /// </para>
    /// <para>
    /// A project of the design set whose output is missing, or older than its sources, is built first:
    /// a generation of outputs that are not there is a generation of nothing, and one of outputs the
    /// code has moved past shows yesterday's types.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the generation is live.</returns>
    /// <exception cref="InvalidOperationException">
    /// The workspace has published nothing, it was not loaded with the build properties, or the host was
    /// started already.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        SolutionSnapshot snapshot = _workspace.CurrentSnapshot
            ?? throw new InvalidOperationException("Load the workspace before starting the design host: a generation is of a snapshot's projects.");

        ThrowUnlessLoadedForDesign(snapshot);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_sync)
            {
                if (_started || _pump is not null)
                {
                    throw new InvalidOperationException("The design host is started already.");
                }

                _resources = ProjectResourceMap.Create(snapshot);
            }

            await _building.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await BuildWhatIsOutOfDateAsync("the designer is starting", cancellationToken).ConfigureAwait(false);
                await CreateGenerationAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _building.Release();
            }

            lock (_sync)
            {
                _started = true;
                _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
            }
        }
        finally
        {
            _turn.Release();
        }

        await ReportStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a part of the designer that holds what a generation built, so that it lets go for a
    /// swap and takes up the successor.
    /// </summary>
    /// <param name="participant">The participant.</param>
    /// <returns>Disposing it unregisters the participant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="participant"/> is <see langword="null"/>.</exception>
    public IDisposable Register(IProjectDesignParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (_sync)
        {
            _participants.Add(participant);
        }

        return new Registration(this, participant);
    }

    /// <summary>
    /// Disposes every document the host opened, reclaims the generation, and stops dealing with changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Work in flight — a batch being dealt with, a build, a swap — is cancelled and waited for. A
    /// window a document's session built is closed, as on any other replacement.
    /// </para>
    /// <para>
    /// The generation is reclaimed rather than only unloaded, because the next host over the same
    /// project in this process is its successor: what Avalonia keeps of it is removed, and the wait is a
    /// reclaim's — bounded, and long only when something still holds it, in which case that next host
    /// says a restart is required instead of loading the types beside it.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes once the host has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workspace.SnapshotChanged -= OnSnapshotChanged;
        Gate.Changed -= OnGateChanged;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Whoever waits for the generation to settle (EnsureBuiltAsync) finds the host disposed.
        SignalStateMoved();

        _work.Writer.TryComplete();
        _buildTimer.Dispose();

        Task[] running;

        lock (_sync)
        {
            running = [.. new[] { _pump, _buildLoop, _swap }.OfType<Task>()];
        }

        foreach (Task task in running)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Every turn is over, and no new one starts: the token is cancelled and the flag is set.
        await _turn.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            DesignDocument[] documents;

            lock (_sync)
            {
                documents = [.. _documents];
                _documents.Clear();
            }

            foreach (DesignDocument document in documents)
            {
                await DisposeDocumentAsync(document).ConfigureAwait(false);
            }

            if (LetGoOfTheGeneration() is { } generation)
            {
                await generation.TryReclaimAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }

        _shutdown.Dispose();
    }

    /// <summary>The projects whose documents a designer shows, and what they reference, in snapshot order.</summary>
    /// <remarks>
    /// A project with <c>AvaloniaXaml</c> items has documents; one that says it is a test project does
    /// not count however many it has, because nobody designs a test's fixtures. What they reference is
    /// in because their types are: a library of controls is where a form's controls live. When nothing
    /// in the snapshot has documents, every project is — a generation still answers a document opened
    /// by path.
    /// </remarks>
    internal static ImmutableArray<ProjectIdentity> DesignSetOf(SolutionSnapshot snapshot)
    {
        var set = new HashSet<ProjectIdentity>();
        var queue = new Queue<ProjectSnapshot>();

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            if (!IsTestProject(project) && project.Items.Any(IsMarkupItem) && set.Add(project.Identity))
            {
                queue.Enqueue(project);
            }
        }

        while (queue.Count > 0)
        {
            foreach (ProjectReferenceInfo reference in queue.Dequeue().ProjectReferences)
            {
                if (!reference.Project.IsEmpty
                    && snapshot.TryGetProject(reference.Project, out ProjectSnapshot? referenced)
                    && set.Add(referenced.Identity))
                {
                    queue.Enqueue(referenced);
                }
            }
        }

        return set.Count == 0
            ? [.. snapshot.Projects.Select(static project => project.Identity)]
            : [.. snapshot.Projects.Select(static project => project.Identity).Where(set.Contains)];
    }

    /// <summary>Whether an item is a document the designer shows: what Avalonia's build compiles as markup.</summary>
    private static bool IsMarkupItem(ProjectItem item) =>
        string.Equals(item.ItemType, "AvaloniaXaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is a markup document's, by its extension — for files no snapshot declares yet.</summary>
    private static bool IsMarkupPath(CanonicalPath path) =>
        path.Extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)
        || path.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestProject(ProjectSnapshot project) =>
        project.Properties.TryGetValue("IsTestProject", out string? value)
        && bool.TryParse(value, out bool isTest)
        && isTest;

    /// <summary>Refuses a snapshot loaded without the properties the host builds with.</summary>
    private void ThrowUnlessLoadedForDesign(SolutionSnapshot snapshot)
    {
        foreach (KeyValuePair<string, string> property in _options.BuildProperties)
        {
            if (!snapshot.Request.GlobalProperties.TryGetValue(property.Key, out string? loaded)
                || !string.Equals(loaded, property.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The workspace was loaded without '{property.Key}={property.Value}', which the design host builds with. "
                    + "Load it with ProjectDesignHostOptions.BuildProperties among its global properties, so that the snapshot "
                    + "names the outputs the designer's builds write.");
            }
        }
    }

    /// <summary>
    /// Creates a generation of the current snapshot's design set, its population, and registers every
    /// document of it — the open ones last, because what they say outranks their files. The caller
    /// holds <see cref="_building"/>.
    /// </summary>
    /// <remarks>
    /// An earlier generation of the same assemblies still in the process — another host's, or one that
    /// would not go — is waited for first, and when it stays there is no generation: the types it would
    /// answer for are the successor's, and only a new process shows them.
    /// </remarks>
    private async Task CreateGenerationAsync(CancellationToken cancellationToken)
    {
        SolutionSnapshot snapshot = _workspace.CurrentSnapshot
            ?? throw new InvalidOperationException("The workspace has no snapshot to make a generation of.");

        ImmutableArray<ProjectIdentity> set = DesignSetOf(snapshot);

        if (!set.IsEmpty
            && !await ProjectAssemblyContext.WaitForPredecessorsAsync(BuiltNames(snapshot, set), cancellationToken).ConfigureAwait(false))
        {
            lock (_sync)
            {
                _designSet = set;
                _resources = ProjectResourceMap.Create(snapshot);
            }

            await RequireRestartAsync(
                ProjectDesignRestartReason.GenerationStillHeld,
                "An earlier generation of the project's types is still held in this process, so the types cannot be loaded "
                    + "beside it. Only a new process shows them as they are now.").ConfigureAwait(false);

            return;
        }

        Born(snapshot, set);

        await RegisterDocumentsAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The simple names of the assemblies a design set's projects build.</summary>
    private static HashSet<string> BuiltNames(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectIdentity project in set)
        {
            foreach (RuntimeAssemblyReference assembly in snapshot.GetRuntimeAssemblies(project))
            {
                if (assembly.Origin is RuntimeAssemblyOrigin.Project or RuntimeAssemblyOrigin.ProjectReference
                    && System.IO.Path.GetFileNameWithoutExtension(assembly.Path.Value) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>Makes the generation, its member resolver and its population, and keeps nothing else of them.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Born(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        if (set.IsEmpty)
        {
            lock (_sync)
            {
                _designSet = [];
                _resources = ProjectResourceMap.Create(snapshot);
                _stale = false;
                _staleReason = null;
            }

            return;
        }

        // Numbered, because a swap after a build that the model did not notice is of the same snapshot,
        // and a generation is told from its predecessor by name in a debugger and in a report.
        string name = string.Join('+', set.Select(project => snapshot.TryGetProject(project, out ProjectSnapshot? found) ? found.Name : project.ToString()))
            + $" @{snapshot.Version} #{++_born}";

        ProjectAssemblyContext generation = ProjectAssemblyContext.Create(snapshot, set, name);
        ProjectXamlPopulation population = ProjectXamlPopulation.Create(
            generation, project => EnvironmentOf(generation, project));

        population.PopulationFailed += OnPopulationFailed;

        lock (_sync)
        {
            _generation = generation;
            _members = new XamlMemberResolver();
            _population = population;
            _designSet = set;
            _resources = ProjectResourceMap.Create(snapshot);
            _environments.Clear();
            _stale = false;
            _staleReason = null;
        }
    }

    /// <summary>
    /// Lets go of the generation and everything the host made of it — population, environments, member
    /// resolver — and answers the generation, for a reclaim to ask about.
    /// </summary>
    /// <remarks>
    /// Synchronous and never inlined, for the reason <c>ProjectAssemblyContext</c> forgets a generation
    /// that way: a local of an asynchronous method lives as long as the method does, and a swap holding
    /// the population in one would hold the types it is proving gone.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ProjectAssemblyContext? LetGoOfTheGeneration()
    {
        ProjectAssemblyContext? generation;
        ProjectXamlPopulation? population;

        lock (_sync)
        {
            generation = _generation;
            population = _population;

            _generation = null;
            _population = null;
            _members = null;
            _environments.Clear();
        }

        if (population is not null)
        {
            population.PopulationFailed -= OnPopulationFailed;
            population.Dispose();
        }

        return generation;
    }

    /// <summary>The map of which file each <c>avares</c> URI names, as the current snapshot has it.</summary>
    private ProjectResourceMap CurrentResources
    {
        get
        {
            lock (_sync)
            {
                return _resources ?? throw new InvalidOperationException("The design host has not been started.");
            }
        }
    }

    /// <summary>The environment a project's documents load in, made once per generation.</summary>
    /// <exception cref="ObjectDisposedException">The generation is not the live one any more.</exception>
    private XamlLoadEnvironment EnvironmentOf(ProjectAssemblyContext generation, ProjectIdentity project)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(!ReferenceEquals(_generation, generation) || generation.IsUnloaded, generation);

            if (!_environments.TryGetValue(project, out XamlLoadEnvironment? environment))
            {
                environment = ProjectXamlEnvironment.Create(generation, project, () => CurrentResources, members: _members);
                _environments[project] = environment;
            }

            return environment;
        }
    }

    /// <summary>The environment and options a document loads with in the live generation, or nothing when there is none.</summary>
    private (XamlLoadEnvironment Environment, XamlLoadOptions Options)? LoadingOf(DesignDocument document)
    {
        ProjectAssemblyContext? generation;

        lock (_sync)
        {
            generation = _generation;
        }

        if (generation is null || generation.IsUnloaded)
        {
            return null;
        }

        try
        {
            return (
                EnvironmentOf(generation, document.Project),
                ProjectXamlEnvironment.CreateOptions(generation, document.Project, XamlLoadMode.Design, rootAccess: document.RootAccess));
        }
        catch (ObjectDisposedException)
        {
            // Replaced between the read and the call; the swap that replaced it attaches the document.
            return null;
        }
    }

    private ProjectDesignState ComputeState() =>
        _disposed != 0 ? ProjectDesignState.Disposed
        : _restartReason is not null ? ProjectDesignState.RestartRequired
        : _swapping ? ProjectDesignState.Swapping
        : !_started ? ProjectDesignState.Starting
        : _building.CurrentCount == 0 ? ProjectDesignState.Building
        : _stale ? ProjectDesignState.SwapPending
        : ProjectDesignState.Live;

    /// <summary>Raises <see cref="StateChanged"/> when the state moved since it was last raised.</summary>
    private async Task ReportStateAsync()
    {
        ProjectDesignState previous;
        ProjectDesignState state;

        lock (_sync)
        {
            previous = _reported;
            state = ComputeState();
            _reported = state;
        }

        if (previous != state)
        {
            SignalStateMoved();

            await RaiseAsync(StateChanged, new ProjectDesignStateChangedEventArgs(previous, state)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Raises an event on the user interface thread, each subscriber on its own, and waits until all ran.
    /// </summary>
    /// <remarks>
    /// A subscriber that throws is reported through <see cref="OperationFailed"/> and does not stop the
    /// others, the isolation ADR 0007 gives the workspace's own subscribers.
    /// </remarks>
    private Task RaiseAsync<T>(EventHandler<T>? handler, T arguments)
        where T : EventArgs
    {
        if (handler is null || Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Raise(handler, arguments)).GetTask();
    }

    /// <summary>Calls each subscriber in turn on the current thread, isolating the ones that throw.</summary>
    private void Raise<T>(EventHandler<T> handler, T arguments)
        where T : EventArgs
    {
        foreach (EventHandler<T> subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, arguments);
            }
            catch (Exception exception) when (arguments is not ProjectDesignFailureEventArgs)
            {
                ReportFailure("raising an event", exception);
            }
            catch (Exception)
            {
                // A subscriber to the failures that fails is not reported to itself.
            }
        }
    }

    /// <summary>Reports a failure on the current thread, which is the user interface thread.</summary>
    private void ReportFailure(string operation, Exception exception)
    {
        if (OperationFailed is { } failed)
        {
            Raise(failed, new ProjectDesignFailureEventArgs(operation, exception));
        }
    }

    /// <summary>Reports a failure from any thread.</summary>
    private Task ReportFailureAsync(string operation, Exception exception) =>
        RaiseAsync(OperationFailed, new ProjectDesignFailureEventArgs(operation, exception));

    private void OnPopulationFailed(object? sender, XamlLivePopulationFailedEventArgs e)
    {
        if (PopulationFailed is { } failed && Volatile.Read(ref _disposed) == 0)
        {
            Dispatcher.UIThread.Post(() => Raise(failed, e));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class Registration(ProjectDesignHost host, IProjectDesignParticipant participant) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (host._sync)
            {
                host._participants.Remove(participant);
            }
        }
    }
}

/// <summary>Raised when a <see cref="ProjectDesignHost"/>'s state moved.</summary>
public sealed class ProjectDesignStateChangedEventArgs : EventArgs
{
    internal ProjectDesignStateChangedEventArgs(ProjectDesignState previous, ProjectDesignState state)
    {
        Previous = previous;
        State = state;
    }

    /// <summary>Gets the state before.</summary>
    public ProjectDesignState Previous { get; }

    /// <summary>Gets the state now.</summary>
    public ProjectDesignState State { get; }
}
