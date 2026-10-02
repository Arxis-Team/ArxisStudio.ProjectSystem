using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// A workspace over the fixtures' project, with a provider whose restores and builds a test steers.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs MSBuild. A build is what the design host can observe of one: it was asked for,
/// with which projects and properties, and whether it rewrote the output the generation was loaded
/// from — which the bench does by moving the output's write time, the way a compiler's write moves it.
/// A restore writes the files a restore writes.
/// </para>
/// <para>
/// An operation can be held: it parks the moment it arrives, the test is told, and it finishes when the
/// test says so — the coordination ADR 0006's tests use, rather than a guess at how long anything takes.
/// </para>
/// </remarks>
internal sealed class DesignBench : IProjectSystemProvider, IProjectOperationProvider, IAsyncDisposable
{
    /// <summary>The properties the host builds with, and the workspace is loaded with.</summary>
    internal static readonly ProjectMetadata DesignProperties = ProjectMetadata.Create(
    [
        new KeyValuePair<string, string>("OutputPath", "bin/ArxisStudio/"),
    ]);

    private readonly Lock _sync = new();
    private readonly List<ProjectOperationRequest> _operations = [];
    private readonly Queue<TaskCompletionSource<HeldOperation>> _arrivals = new();
    private readonly Queue<HeldOperation> _parked = new();

    internal DesignBench(DesignFixtures fixtures)
    {
        Fixtures = fixtures;
        Workspace = new ProjectWorkspace(this);
        Snapshot = request => fixtures.Snapshot(request);
    }

    /// <summary>Gets the project on disk.</summary>
    internal DesignFixtures Fixtures { get; }

    /// <summary>Gets the workspace, loaded by <see cref="LoadAsync(CancellationToken)"/>.</summary>
    internal ProjectWorkspace Workspace { get; }

    /// <summary>Gets the clock the host's build delay runs on.</summary>
    internal FakeTimeProvider Clock { get; } = new();

    /// <summary>Gets or sets what a load answers.</summary>
    internal Func<WorkspaceLoadRequest, SolutionSnapshot> Snapshot { get; set; }

    /// <summary>Gets or sets whether a build fails.</summary>
    internal bool FailBuilds { get; set; }

    /// <summary>Gets or sets whether a build rewrites the output, as one that compiled something does.</summary>
    internal bool BuildRewritesOutput { get; set; } = true;

    /// <summary>Gets or sets whether operations park until the test lets them go.</summary>
    internal bool HoldOperations { get; set; }

    /// <summary>Gets the operations asked for, in order.</summary>
    internal ImmutableArray<ProjectOperationRequest> Operations
    {
        get
        {
            lock (_sync)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>Gets the kinds of the operations asked for, in order.</summary>
    internal ImmutableArray<ProjectOperationKind> Kinds => [.. Operations.Select(static operation => operation.Kind)];

    /// <inheritdoc />
    public string Name => "DesignBench";

    /// <summary>Loads the workspace as a designer does: with the build properties among the global ones.</summary>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        WorkspaceLoadResult result = await Workspace.LoadAsync(
            Options().CreateLoadRequest(Workspace, Fixtures.ProjectFile),
            cancellationToken);

        if (result.Status == WorkspaceLoadStatus.Failed)
        {
            throw new InvalidOperationException("The bench's own load failed: " + string.Join("; ", result.Diagnostics));
        }
    }

    /// <summary>The options a host on this bench runs with: its clock, and the build properties.</summary>
    internal ProjectDesignHostOptions Options(TimeSpan? delay = null) => new()
    {
        BuildProperties = DesignProperties,
        BuildDelay = delay ?? TimeSpan.FromMilliseconds(400),
        TimeProvider = Clock,
    };

    /// <summary>Completes when the next held operation arrives and parks.</summary>
    internal Task<HeldOperation> NextArrivalAsync()
    {
        lock (_sync)
        {
            if (_parked.Count > 0)
            {
                return Task.FromResult(_parked.Dequeue());
            }

            var arrival = new TaskCompletionSource<HeldOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

            _arrivals.Enqueue(arrival);

            return arrival.Task;
        }
    }

    /// <inheritdoc />
    public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

    /// <inheritdoc />
    public ValueTask<WorkspaceLoadResult> LoadAsync(WorkspaceLoadRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(WorkspaceLoadResult.Success(Snapshot(request)));

    /// <inheritdoc />
    public bool CanExecute(ProjectOperationKind kind) => kind is ProjectOperationKind.Restore or ProjectOperationKind.Build;

    /// <inheritdoc />
    public async ValueTask<ProjectOperationResult> ExecuteAsync(
        ProjectOperationRequest request,
        IProgress<ProjectOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        HeldOperation? held = null;
        TaskCompletionSource<HeldOperation>? arrival = null;

        lock (_sync)
        {
            _operations.Add(request);

            if (HoldOperations)
            {
                held = new HeldOperation(request);

                if (_arrivals.Count > 0)
                {
                    arrival = _arrivals.Dequeue();
                }
                else
                {
                    _parked.Enqueue(held);
                }
            }
        }

        if (held is not null)
        {
            arrival?.TrySetResult(held);

            await held.Released.WaitAsync(cancellationToken);
        }

        if (request.Kind == ProjectOperationKind.Restore)
        {
            foreach (CanonicalPath output in RestoreOutputs())
            {
                Directory.CreateDirectory(output.Directory.Value);
                await File.WriteAllTextAsync(output.Value, "{}", cancellationToken);
            }

            return ProjectOperationResult.Succeeded();
        }

        if (FailBuilds)
        {
            return ProjectOperationResult.Failed(new ProjectDiagnostic("CS0103", "The name 'Nothing' does not exist.", ProjectDiagnosticSeverity.Error));
        }

        if (BuildRewritesOutput)
        {
            Rewrite(Fixtures.Output);
        }

        return ProjectOperationResult.Succeeded();
    }

    /// <summary>Moves a file's write time on, as a compiler's write would.</summary>
    internal static void Rewrite(CanonicalPath file)
    {
        DateTime written = File.GetLastWriteTimeUtc(file.Value);

        File.SetLastWriteTimeUtc(file.Value, written.AddSeconds(2));
    }

    private IEnumerable<CanonicalPath> RestoreOutputs() =>
        Workspace.CurrentSnapshot?.Projects.SelectMany(static project => project.RestoreOutputs) ?? [];

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            HoldOperations = false;

            foreach (HeldOperation held in _parked)
            {
                held.Release();
            }
        }

        await Workspace.DisposeAsync();
    }
}

/// <summary>A restore or build parked inside the bench until the test lets it go.</summary>
internal sealed class HeldOperation(ProjectOperationRequest request)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets what was asked.</summary>
    internal ProjectOperationRequest Request { get; } = request;

    internal Task Released => _released.Task;

    /// <summary>Lets the operation finish.</summary>
    internal void Release() => _released.TrySetResult();
}
