using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// The workspace's behaviour under load, failure, cancellation and disposal.
/// </summary>
/// <remarks>
/// Coordination is always a <see cref="ControllableProvider"/> parked inside a load, never a delay.
/// Where a test asserts that something has <em>not</em> happened, it can do so because the window it
/// is asserting inside is held open by the test itself.
/// </remarks>
public sealed class ProjectWorkspaceTests
{
    [Fact]
    public async Task AnInitialLoad_PublishesTheFirstVersion()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        var events = new List<WorkspaceChangedEventArgs>();
        workspace.SnapshotChanged += (_, e) => events.Add(e);

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();
        arrival.CompleteWith(SnapshotFor(arrival.Request));

        WorkspaceLoadResult result = await load;

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.Same(result.Snapshot, workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentSnapshot!.Version);
        Assert.Single(events);
        Assert.Null(events[0].Previous);
    }

    [Fact]
    public async Task AFailedInitialLoad_PublishesNothing()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        int raised = 0;
        workspace.SnapshotChanged += (_, _) => raised++;

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        (await provider.NextArrivalAsync()).Fail();

        WorkspaceLoadResult result = await load;

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Null(workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.None, workspace.CurrentVersion);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ARefresh_PublishesOneNewSnapshot()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        var events = new List<WorkspaceChangedEventArgs>();
        workspace.SnapshotChanged += (_, e) => events.Add(e);

        SolutionSnapshot first = await LoadAsync(workspace, provider);
        SolutionSnapshot second = await RefreshAsync(workspace, provider);

        Assert.Equal(new WorkspaceVersion(2), workspace.CurrentVersion);
        Assert.Same(second, workspace.CurrentSnapshot);
        Assert.Equal(2, events.Count);
        Assert.Same(first, events[1].Previous);
    }

    /// <summary>
    /// A project keeps its identity across a refresh, which is what makes anything a consumer
    /// remembers about a project — expanded nodes, an open editor, a cached analysis — survive one.
    /// </summary>
    /// <remarks>
    /// This falls out of identities being derived from the workspace and the canonical project path
    /// rather than minted per load, and it is asserted because it is the kind of property that is
    /// easy to break later while every other test still passes.
    /// </remarks>
    [Fact]
    public async Task ARefresh_KeepsEveryProjectsIdentity()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider);
        SolutionSnapshot second = await RefreshAsync(workspace, provider);

        Assert.NotSame(first, second);

        Assert.Equal(
            first.Projects.Select(static p => p.Identity),
            second.Projects.Select(static p => p.Identity));

        foreach (ProjectSnapshot project in first.Projects)
        {
            Assert.True(second.TryGetProject(project.Identity, out ProjectSnapshot? refreshed));
            Assert.Equal(project.ProjectFilePath, refreshed.ProjectFilePath);
        }
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheSnapshotAndVersion()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider);

        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        (await provider.NextArrivalAsync()).Fail();

        Assert.Equal(WorkspaceLoadStatus.Failed, (await refresh).Status);
        Assert.Same(first, workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
    }

    [Fact]
    public async Task ACancelledRefresh_KeepsTheSnapshotAndVersion()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider);

        using var cancellation = new CancellationTokenSource();
        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(cancellation.Token).AsTask();

        await provider.NextArrivalAsync();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Same(first, workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.False(workspace.IsMutationHeld);
    }

    /// <summary>
    /// A provider that ignores its token and returns a perfectly good snapshot anyway. The caller
    /// asked for this to stop, so it must not advance the version.
    /// </summary>
    [Fact]
    public async Task AProviderThatIgnoresCancellation_StillDoesNotPublish()
    {
        var provider = new ControllableProvider { ObservesCancellation = false };
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider);

        using var cancellation = new CancellationTokenSource();
        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(cancellation.Token).AsTask();

        ControllableLoad arrival = await provider.NextArrivalAsync();
        await cancellation.CancelAsync();
        arrival.CompleteWith(SnapshotFor(arrival.Request));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Same(first, workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
    }

    /// <summary>
    /// The window this asserts inside is held open by the test: the provider is parked, so "while a
    /// new one is being built" is a state the test controls rather than races.
    /// </summary>
    [Fact]
    public async Task AReaderKeepsTheOldSnapshotWhileANewOneIsBuilt()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider, projectCount: 3);

        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        // The provider is now parked inside the mutation boundary.
        Assert.Same(first, workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.Equal(3, workspace.CurrentSnapshot!.Projects.Length);

        foreach (ProjectSnapshot project in workspace.CurrentSnapshot.Projects)
        {
            Assert.NotEmpty(project.Name);
        }

        arrival.CompleteWith(SnapshotFor(arrival.Request, projectCount: 2));
        await refresh;

        Assert.Equal(2, workspace.CurrentSnapshot!.Projects.Length);

        // The reference captured before the swap still describes what it always did.
        Assert.Equal(3, first.Projects.Length);
        Assert.Equal(WorkspaceVersion.Initial, first.Version);
    }

    /// <summary>
    /// The reason the gate is a queue rather than a semaphore: a semaphore gives exclusion and says
    /// nothing about order, so an older refresh released last would publish stale state.
    /// </summary>
    [Fact]
    public async Task ConcurrentRefreshesRunInTheOrderTheyArrived()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        await LoadAsync(workspace, provider);

        // Notification delivery is explicitly unordered -- the gate is released before the raise --
        // so this collects rather than sequences, and a thread-safe collection is not optional.
        var published = new ConcurrentQueue<long>();
        workspace.SnapshotChanged += (_, e) => published.Enqueue(e.Version.Value);

        int alreadyStarted = provider.Started;
        string[] names = ["One", "Two", "Three", "Four"];
        var refreshes = new List<Task<WorkspaceLoadResult>>();

        foreach (string name in names)
        {
            refreshes.Add(workspace
                .LoadAsync(Request(workspace) with { Configuration = name }, TestContext.Current.CancellationToken)
                .AsTask());
        }

        // Exactly one of the four got past the gate. The other three are queued, which is the
        // whole claim -- and it is deterministic because the one that got through is parked.
        ControllableLoad first = await provider.NextArrivalAsync();
        Assert.Equal(alreadyStarted + 1, provider.Started);

        Assert.Equal(names[0], first.Request.Configuration);
        first.CompleteWith(SnapshotFor(first.Request, name: first.Request.Configuration!));

        // The guarantee under test: the provider is handed the requests in submission order, not
        // in whatever order the thread pool happens to release the waiters.
        for (int index = 1; index < names.Length; index++)
        {
            ControllableLoad arrival = await provider.NextArrivalAsync();

            Assert.Equal(names[index], arrival.Request.Configuration);

            arrival.CompleteWith(SnapshotFor(arrival.Request, name: arrival.Request.Configuration!));
        }

        await Task.WhenAll(refreshes);

        Assert.Equal(new WorkspaceVersion(5), workspace.CurrentVersion);

        // Every publication was announced exactly once. Which order they arrived in is not a
        // promise this library makes, so it is not a thing this test asserts.
        Assert.Equal([2L, 3L, 4L, 5L], published.Order());
    }

    [Fact]
    public async Task VersionsIncreaseByOneOnEveryPublication()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        var versions = new List<long>();
        workspace.SnapshotChanged += (_, e) => versions.Add(e.Version.Value);

        await LoadAsync(workspace, provider);
        await RefreshAsync(workspace, provider);
        await RefreshAsync(workspace, provider);

        Assert.Equal([1L, 2L, 3L], versions);
    }

    [Fact]
    public async Task AProviderThatThrows_BecomesADiagnosticAndReleasesTheGate()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> first = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        Task<WorkspaceLoadResult> second = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();

        (await provider.NextArrivalAsync()).Throw(new InvalidOperationException("the provider is broken"));

        WorkspaceLoadResult failed = await first;

        Assert.Equal(WorkspaceLoadStatus.Failed, failed.Status);
        Assert.Equal(ProjectDiagnosticCodes.ProviderFailed, failed.Diagnostics[0].Code);
        Assert.Contains("broken", failed.Diagnostics[0].Message, StringComparison.Ordinal);

        // The gate was released, so the queued load gets its turn.
        ControllableLoad next = await provider.NextArrivalAsync();
        next.CompleteWith(SnapshotFor(next.Request));

        Assert.Equal(WorkspaceLoadStatus.Succeeded, (await second).Status);
        Assert.False(workspace.IsMutationHeld);
    }

    [Fact]
    public async Task CancellingOneWaiter_LetsTheRestThrough()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> first = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad held = await provider.NextArrivalAsync();

        using var cancellation = new CancellationTokenSource();
        Task<WorkspaceLoadResult> middle = workspace.LoadAsync(Request(workspace), cancellation.Token).AsTask();
        Task<WorkspaceLoadResult> last = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();

        await cancellation.CancelAsync();
        held.CompleteWith(SnapshotFor(held.Request));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middle);

        ControllableLoad lastArrival = await provider.NextArrivalAsync();
        lastArrival.CompleteWith(SnapshotFor(lastArrival.Request));

        Assert.Equal(WorkspaceLoadStatus.Succeeded, (await first).Status);
        Assert.Equal(WorkspaceLoadStatus.Succeeded, (await last).Status);
        Assert.Equal(new WorkspaceVersion(2), workspace.CurrentVersion);
        Assert.False(workspace.IsMutationHeld);
    }

    [Fact]
    public async Task DisposalWaitsForWorkInFlightAndRefusesWhatIsQueuedBehindIt()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot first = await LoadAsync(workspace, provider);

        Task<WorkspaceLoadResult> inFlight = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        Task<WorkspaceLoadResult> queued = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        Task disposal = workspace.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.False(inFlight.IsCompleted);

        arrival.CompleteWith(SnapshotFor(arrival.Request));

        // In-flight work finishes and publishes: disposal was waiting for exactly that.
        Assert.Equal(WorkspaceLoadStatus.Succeeded, (await inFlight).Status);
        Assert.Equal(new WorkspaceVersion(2), workspace.CurrentVersion);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await disposal;

        // Readers keep working after disposal; mutations do not.
        Assert.NotNull(workspace.CurrentSnapshot);
        Assert.NotSame(first, workspace.CurrentSnapshot);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposingTwice_IsSafe()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        await LoadAsync(workspace, provider);

        await workspace.DisposeAsync();
        await workspace.DisposeAsync();
    }

    [Fact]
    public async Task AHandlerSeesThePublishedStateAndAFreeGate()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        SolutionSnapshot? seen = null;
        SolutionSnapshot? argument = null;
        WorkspaceVersion seenVersion = default;
        WorkspaceVersion argumentVersion = default;
        bool gateHeld = true;

        // Everything is recorded and asserted afterwards, never asserted inside the handler: the
        // workspace isolates a throwing subscriber, so a failed Assert in here would be swallowed
        // and the test would pass while proving nothing.
        workspace.SnapshotChanged += (_, e) =>
        {
            seen = workspace.CurrentSnapshot;
            seenVersion = workspace.CurrentVersion;
            gateHeld = workspace.IsMutationHeld;
            argument = e.Snapshot;
            argumentVersion = e.Version;
        };

        SolutionSnapshot published = await LoadAsync(workspace, provider);

        Assert.Same(published, seen);
        Assert.Same(published, argument);
        Assert.Equal(WorkspaceVersion.Initial, seenVersion);
        Assert.Equal(WorkspaceVersion.Initial, argumentVersion);
        Assert.False(gateHeld);
    }

    /// <summary>
    /// The documented policy: every subscriber runs, none of them can stop another, and the
    /// exception does not reach the caller of a load that succeeded.
    /// </summary>
    [Fact]
    public async Task AThrowingHandler_StarvesNobodyAndReachesNobody()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        bool middleRan = false;
        bool lastRan = false;

        workspace.SnapshotChanged += (_, _) => throw new InvalidOperationException("first handler is broken");
        workspace.SnapshotChanged += (_, _) => middleRan = true;
        workspace.SnapshotChanged += (_, _) => throw new ArgumentException("third handler is broken too");
        workspace.SnapshotChanged += (_, _) => lastRan = true;

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();
        arrival.CompleteWith(SnapshotFor(arrival.Request));

        WorkspaceLoadResult result = await load;

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.True(middleRan);
        Assert.True(lastRan);
        Assert.NotNull(workspace.CurrentSnapshot);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.False(workspace.IsMutationHeld);

        // Still usable afterwards.
        await RefreshAsync(workspace, provider);
        Assert.Equal(new WorkspaceVersion(2), workspace.CurrentVersion);
    }

    [Fact]
    public async Task NoProviderThatCanOpenIt_IsADiagnosticNotAnException()
    {
        var provider = new ControllableProvider { Accepts = false };
        var workspace = new ProjectWorkspace(provider);

        WorkspaceLoadResult result = await workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(ProjectDiagnosticCodes.UnsupportedEntryPoint, result.Diagnostics[0].Code);
        Assert.Equal(0, provider.Started);
    }

    [Fact]
    public async Task AProviderThatThrowsFromCanLoad_IsADiagnostic()
    {
        var provider = new ControllableProvider { CanLoadThrows = true };
        var workspace = new ProjectWorkspace(provider);

        WorkspaceLoadResult result = await workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(ProjectDiagnosticCodes.ProviderFailed, result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task ProvidersAreTriedInOrder()
    {
        var first = new ControllableProvider { Name = "First", Accepts = false };
        var second = new ControllableProvider { Name = "Second" };

        var workspace = new ProjectWorkspace([first, second]);

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await second.NextArrivalAsync();
        arrival.CompleteWith(SnapshotFor(arrival.Request));

        Assert.Equal(WorkspaceLoadStatus.Succeeded, (await load).Status);
        Assert.Equal(0, first.Started);
        Assert.Equal(1, second.Started);
    }

    /// <summary>
    /// A snapshot stamped with another workspace's identity would silently break the determinism
    /// project identity exists to provide, so it is refused at the boundary rather than trusted.
    /// </summary>
    [Fact]
    public async Task ASnapshotFromAnotherWorkspace_IsRefused()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        arrival.CompleteWith(SnapshotFor(arrival.Request with { Workspace = WorkspaceIdentity.New() }));

        WorkspaceLoadResult result = await load;

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(ProjectDiagnosticCodes.InvalidProviderResult, result.Diagnostics[0].Code);
        Assert.Null(workspace.CurrentSnapshot);
    }

    [Fact]
    public async Task ASnapshotForADifferentEntryPoint_IsRefused()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        arrival.CompleteWith(SnapshotFor(arrival.Request with { EntryPointPath = TestPaths.Project("Elsewhere") }));

        WorkspaceLoadResult result = await load;

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(ProjectDiagnosticCodes.InvalidProviderResult, result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task TheWorkspaceStampsItsOwnIdentityOnEveryRequest()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> load = workspace
            .LoadAsync(new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = TestPaths.Project(),
            }, TestContext.Current.CancellationToken)
            .AsTask();

        ControllableLoad arrival = await provider.NextArrivalAsync();

        Assert.Equal(workspace.Identity, arrival.Request.Workspace);

        arrival.CompleteWith(SnapshotFor(arrival.Request));
        await load;
    }

    [Fact]
    public async Task RefreshingBeforeAnythingIsLoaded_Throws()
    {
        var workspace = new ProjectWorkspace(new ControllableProvider());

        Assert.Null(workspace.CurrentRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await workspace.RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARefreshRepeatsTheRequestTheSnapshotAnswers()
    {
        var provider = new ControllableProvider();
        var workspace = new ProjectWorkspace(provider);

        Task<WorkspaceLoadResult> load = workspace
            .LoadAsync(Request(workspace) with { Configuration = "Release" }, TestContext.Current.CancellationToken)
            .AsTask();

        ControllableLoad first = await provider.NextArrivalAsync();
        first.CompleteWith(SnapshotFor(first.Request));
        await load;

        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        ControllableLoad second = await provider.NextArrivalAsync();

        Assert.Equal("Release", second.Request.Configuration);

        second.CompleteWith(SnapshotFor(second.Request));
        await refresh;
    }

    [Fact]
    public void AWorkspaceNeedsAtLeastOneProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectWorkspace((IProjectSystemProvider)null!));
        Assert.Throws<ArgumentNullException>(() => new ProjectWorkspace((IEnumerable<IProjectSystemProvider>)null!));
        Assert.Throws<ArgumentException>(() => new ProjectWorkspace([]));
        Assert.Throws<ArgumentException>(() => new ProjectWorkspace([null!]));
    }

    [Fact]
    public void EveryWorkspaceHasItsOwnIdentity()
    {
        using var _ = new CancellationTokenSource();

        var first = new ProjectWorkspace(new ControllableProvider());
        var second = new ProjectWorkspace(new ControllableProvider());

        Assert.NotEqual(first.Identity, second.Identity);
        Assert.False(first.Identity.IsEmpty);
    }

    private static WorkspaceLoadRequest Request(ProjectWorkspace workspace) => new()
    {
        Workspace = workspace.Identity,
        EntryPointPath = TestPaths.Project(),
    };

    private static SolutionSnapshot SnapshotFor(
        WorkspaceLoadRequest request,
        string name = "App",
        int projectCount = 1)
    {
        var builder = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,
            Name = name,
            Request = request,
        };

        for (int index = 0; index < projectCount; index++)
        {
            CanonicalPath path = index == 0
                ? request.EntryPointPath
                : request.EntryPointPath.Directory.Combine($"Project{index}.csproj");

            builder.Projects.Add(new ProjectSnapshotBuilder
            {
                Identity = ProjectIdentity.Create(request.Workspace, path),
                Name = $"{name}{index}",
                ProjectFilePath = path,
            }.ToSnapshot());
        }

        return builder.ToSnapshot();
    }

    private static async Task<SolutionSnapshot> LoadAsync(
        ProjectWorkspace workspace,
        ControllableProvider provider,
        int projectCount = 1)
    {
        Task<WorkspaceLoadResult> load = workspace.LoadAsync(Request(workspace), TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        arrival.CompleteWith(SnapshotFor(arrival.Request, projectCount: projectCount));

        return (await load).Snapshot!;
    }

    private static async Task<SolutionSnapshot> RefreshAsync(
        ProjectWorkspace workspace,
        ControllableProvider provider)
    {
        Task<WorkspaceLoadResult> refresh = workspace.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        ControllableLoad arrival = await provider.NextArrivalAsync();

        arrival.CompleteWith(SnapshotFor(arrival.Request));

        return (await refresh).Snapshot!;
    }
}
