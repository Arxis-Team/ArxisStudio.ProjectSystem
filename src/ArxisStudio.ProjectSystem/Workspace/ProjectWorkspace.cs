using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Owns the current snapshot of an opened solution or project, its version, and the order in which
/// loads and refreshes happen.
/// </summary>
/// <remarks>
/// <para>
/// All published state lives in one immutable object. A mutation builds a replacement and swaps one
/// reference; readers take the current one with no lock at all. A reader therefore observes the
/// workspace either wholly before or wholly after a change.
/// </para>
/// <para>
/// <b>Reading a pair.</b> <see cref="CurrentSnapshot"/>, <see cref="CurrentVersion"/> and
/// <see cref="CurrentRequest"/> are three separate reads, so two of them can straddle a
/// publication. When both a snapshot and its version are wanted, read
/// <see cref="CurrentSnapshot"/> once and use <see cref="SolutionSnapshot.Version"/>: that pair
/// comes from one publication and cannot disagree.
/// </para>
/// <para>
/// <b>Order.</b> Loads and refreshes take one mutation boundary in the order the requests arrive,
/// and run one at a time. There is no coalescing: <em>N</em> queued refreshes run <em>N</em>
/// provider loads. Debouncing belongs with the file-change events that make it necessary, which is
/// a later milestone.
/// </para>
/// <para>
/// <b>Failure.</b> A failed load, a provider that throws, and a cancelled load all leave the
/// previous snapshot and version exactly as they were. The version advances only on publication,
/// which is what makes an unchanged version mean "nothing changed" rather than "nobody looked".
/// </para>
/// </remarks>
public sealed class ProjectWorkspace : IAsyncDisposable
{
    private readonly WorkspaceMutationGate _gate = new();

    private ProjectWorkspaceState _state = ProjectWorkspaceState.Empty;

    /// <summary>
    /// Disposal, as an <see cref="int"/> rather than a <see cref="bool"/>.
    /// </summary>
    /// <remarks>
    /// Disposal and the operations it races are on different threads by design, and an
    /// unsynchronised read of a field another thread just wrote is not a read of anything in
    /// particular. Going through <see cref="Interlocked"/> is also what makes disposing twice safe
    /// rather than accidentally safe. This is ADR 0011's second finding, and it was a real bug.
    /// </remarks>
    private int _disposed;

    /// <summary>Creates a workspace served by one provider.</summary>
    /// <param name="provider">The provider to load through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public ProjectWorkspace(IProjectSystemProvider provider)
        : this(ToArray(provider))
    {
    }

    /// <summary>Creates a workspace served by several providers, tried in order.</summary>
    /// <param name="providers">The providers to try, most specific first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The sequence is empty or contains a <see langword="null"/>.</exception>
    public ProjectWorkspace(IEnumerable<IProjectSystemProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // Materialised now: a workspace whose provider list could change under it could pick a
        // different provider for a refresh than it used for the load it is refreshing.
        ImmutableArray<IProjectSystemProvider> materialised = [.. providers];

        if (materialised.IsEmpty)
        {
            throw new ArgumentException("A workspace needs at least one provider.", nameof(providers));
        }

        foreach (IProjectSystemProvider provider in materialised)
        {
            if (provider is null)
            {
                throw new ArgumentException("A provider cannot be null.", nameof(providers));
            }
        }

        Providers = materialised;
        Identity = WorkspaceIdentity.New();
    }

    /// <summary>
    /// Raised after a snapshot is published.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised outside the mutation boundary and after the swap, so a handler may read the workspace
    /// freely — including starting another load — and finds the new snapshot already current.
    /// </para>
    /// <para>
    /// <b>Notifications are not ordered against each other.</b> The boundary is released before this
    /// is raised, which is what lets a handler start another load without deadlocking — and it means
    /// the next publication can happen while this notification is still being delivered. Two handlers
    /// can therefore be running for two versions at once, and nothing promises the older one finishes
    /// first. Publication itself is strictly ordered; only delivery is not.
    /// </para>
    /// <para>
    /// A handler that keeps derived state must therefore compare
    /// <see cref="WorkspaceChangedEventArgs.Version"/> with the version it last saw and ignore
    /// anything older, rather than assume the newest notification is the newest snapshot. That is
    /// what the version is for.
    /// </para>
    /// <para>
    /// <b>A handler that throws is isolated.</b> Every other handler still runs, and the exception
    /// does not reach whoever called <see cref="LoadAsync"/> or <see cref="RefreshAsync"/>. Two
    /// reasons, and the second is the decisive one. Subscribers here are independent tools — a
    /// project tree, an error list, an analysis host — and one broken handler must not leave the
    /// others permanently unaware that the solution reloaded. And the caller of a <em>successful</em>
    /// load is the wrong party to hand a third party's bug to: it did not write the handler, cannot
    /// fix it, and would lose the result of an operation that worked, since a throw happens instead
    /// of a return.
    /// </para>
    /// <para>
    /// The cost is that a handler's failure is silent. A handler that wants its own failures
    /// reported should catch them itself — that is one line, in the code that knows what to do
    /// about it. See <c>docs/adr/0007-a-throwing-subscriber-is-isolated.md</c>.
    /// </para>
    /// </remarks>
    public event EventHandler<WorkspaceChangedEventArgs>? SnapshotChanged;

    /// <summary>Gets this workspace's identity, which every identity it hands out carries.</summary>
    public WorkspaceIdentity Identity { get; }

    /// <summary>Gets the providers this workspace loads through, in the order they are tried.</summary>
    public ImmutableArray<IProjectSystemProvider> Providers { get; }

    /// <summary>Gets the current snapshot, or <see langword="null"/> before the first publication.</summary>
    /// <remarks>Readable after disposal: a read of an immutable object is safe forever, and a tool
    /// tearing down still has to be able to show what it was holding.</remarks>
    public SolutionSnapshot? CurrentSnapshot => Volatile.Read(ref _state).Snapshot;

    /// <summary>Gets the version of the current snapshot.</summary>
    public WorkspaceVersion CurrentVersion => Volatile.Read(ref _state).Version;

    /// <summary>Gets the request the current snapshot answers, which <see cref="RefreshAsync"/> repeats.</summary>
    public WorkspaceLoadRequest? CurrentRequest => Volatile.Read(ref _state).Request;

    /// <summary>Loads an entry point, replacing whatever this workspace had open.</summary>
    /// <param name="request">What to open. Its workspace identity is replaced with this workspace's.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The result. A snapshot is published unless the result failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The workspace was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public ValueTask<WorkspaceLoadResult> LoadAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MutateAsync(request with { Workspace = Identity }, cancellationToken);
    }

    /// <summary>Reloads the current entry point.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The result. A snapshot is published unless the result failed.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been loaded yet.</exception>
    /// <exception cref="ObjectDisposedException">The workspace was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public ValueTask<WorkspaceLoadResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(request: null, cancellationToken);

    /// <summary>
    /// Waits for work in flight to finish, then refuses further mutations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asynchronous for exactly one reason: to let a host wait for a provider to finish before
    /// tearing that provider down. An operation already inside the mutation boundary runs to
    /// completion and publishes — its result is complete and correct, and disposal is waiting
    /// precisely so it can finish. An operation still queued behind it gets
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// The providers are not disposed. This workspace did not create them.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when nothing is in flight.</returns>
    public async ValueTask DisposeAsync()
    {
        // Marked first, so anything queued behind the wait below finds the workspace gone rather
        // than running against one that is being torn down.
        Interlocked.Exchange(ref _disposed, 1);

        // Without a token on purpose: this is waiting for work that must be allowed to finish.
        // The gate itself is never disposed, so a queued operation can still take its turn and be
        // told the workspace is gone.
        using WorkspaceMutationGate.WorkspaceMutationLease lease =
            await _gate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Whether anything holds the mutation boundary. For tests; see the gate.</summary>
    internal bool IsMutationHeld => _gate.IsHeld;

    private static ImmutableArray<IProjectSystemProvider> ToArray(IProjectSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return [provider];
    }

    private async ValueTask<WorkspaceLoadResult> MutateAsync(
        WorkspaceLoadRequest? request,
        CancellationToken cancellationToken)
    {
        WorkspaceChangedEventArgs notification;
        WorkspaceLoadResult result;

        // The first await, deliberately. An async method runs synchronously to it, so by the time
        // the caller holds this task the waiter is already queued -- which is what makes arrival
        // order the caller's submission order rather than a race with the thread pool.
        using (WorkspaceMutationGate.WorkspaceMutationLease lease =
            await _gate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // Inside the gate, and the answer read on the way in is not consulted at all. A check
            // before the gate is a guess about a workspace somebody else may be in the middle of
            // changing. ADR 0011's first finding.
            ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _disposed, 0, 0) != 0, this);

            ProjectWorkspaceState state = Volatile.Read(ref _state);

            WorkspaceLoadRequest effective = request
                ?? state.Request
                ?? throw new InvalidOperationException(
                    "There is nothing to refresh: this workspace has not loaded anything yet. " +
                    $"Check {nameof(CurrentRequest)} before offering a refresh.");

            result = await LoadThroughProviderAsync(effective, cancellationToken).ConfigureAwait(false);

            // A provider that ignored its token and came back with a good snapshot must still not
            // advance the version: the caller asked for this to stop.
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Snapshot is null)
            {
                return result;
            }

            WorkspaceVersion version = state.Version.Next();
            SolutionSnapshot published = result.Snapshot.WithVersion(version);

            Volatile.Write(ref _state, new ProjectWorkspaceState(published, version, effective));

            result = result.WithSnapshot(published);
            notification = new WorkspaceChangedEventArgs(published, state.Snapshot);
        }

        // Gate released, state published. Only now does arbitrary user code run.
        Raise(notification);

        return result;
    }

    private async ValueTask<WorkspaceLoadResult> LoadThroughProviderAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken)
    {
        IProjectSystemProvider? provider;

        try
        {
            provider = SelectProvider(request.EntryPoint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(ProjectDiagnosticCodes.ProviderFailed, $"A provider failed while being asked whether it could open '{request.EntryPointPath}': {exception.GetType().Name}: {exception.Message}");
        }

        if (provider is null)
        {
            return Failed(
                ProjectDiagnosticCodes.UnsupportedEntryPoint,
                $"No configured provider can open '{request.EntryPointPath}'.");
        }

        WorkspaceLoadResult result;

        try
        {
            result = await provider.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's decision, not a fault, and never becomes a diagnostic.
            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                ProjectDiagnosticCodes.ProviderFailed,
                $"Provider '{provider.Name}' threw while loading '{request.EntryPointPath}': " +
                $"{exception.GetType().Name}: {exception.Message}",
                provider.Name);
        }

        return Validate(result, request, provider);
    }

    /// <summary>
    /// Checks what a provider returned before any of it is published.
    /// </summary>
    /// <remarks>
    /// The snapshot itself needs no copying — it is immutable and core-owned already, because a
    /// builder was the only way to make it. What is checked here is the shapes that would silently
    /// corrupt identity determinism if they were published.
    /// </remarks>
    private WorkspaceLoadResult Validate(
        WorkspaceLoadResult? result,
        WorkspaceLoadRequest request,
        IProjectSystemProvider provider)
    {
        if (result is null)
        {
            return Failed(
                ProjectDiagnosticCodes.InvalidProviderResult,
                $"Provider '{provider.Name}' returned no result.",
                provider.Name);
        }

        if (result.Snapshot is not { } snapshot)
        {
            return result;
        }

        if (snapshot.Workspace != Identity)
        {
            return Failed(
                ProjectDiagnosticCodes.InvalidProviderResult,
                $"Provider '{provider.Name}' returned a snapshot for workspace '{snapshot.Workspace}', " +
                $"but this workspace is '{Identity}'.",
                provider.Name);
        }

        if (snapshot.Request.EntryPointPath != request.EntryPointPath)
        {
            return Failed(
                ProjectDiagnosticCodes.InvalidProviderResult,
                $"Provider '{provider.Name}' was asked for '{request.EntryPointPath}' but returned a " +
                $"snapshot for '{snapshot.Request.EntryPointPath}'.",
                provider.Name);
        }

        var seen = new HashSet<ProjectIdentity>();

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            if (project.Identity.Workspace != Identity)
            {
                return Failed(
                    ProjectDiagnosticCodes.InvalidProviderResult,
                    $"Provider '{provider.Name}' returned project '{project.Name}' with an identity in " +
                    $"workspace '{project.Identity.Workspace}', but this workspace is '{Identity}'.",
                    provider.Name);
            }

            if (!seen.Add(project.Identity))
            {
                return Failed(
                    ProjectDiagnosticCodes.InvalidProviderResult,
                    $"Provider '{provider.Name}' returned two projects with the identity " +
                    $"'{project.Identity}'.",
                    provider.Name);
            }
        }

        return result;
    }

    private static WorkspaceLoadResult Failed(string code, string message, string? providerName = null) =>
        WorkspaceLoadResult.Failure(
            new ProjectDiagnostic(code, message, ProjectDiagnosticSeverity.Error) { ProviderName = providerName });

    private IProjectSystemProvider? SelectProvider(WorkspaceEntryPoint entryPoint)
    {
        foreach (IProjectSystemProvider provider in Providers)
        {
            if (provider.CanLoad(entryPoint))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Tells every subscriber, and lets none of them stop the others.
    /// </summary>
    /// <remarks>
    /// The isolation is the documented policy, and the invocation list is walked by hand rather
    /// than through the event's own multicast dispatch precisely so that one failure does not end
    /// the walk. See the remarks on <see cref="SnapshotChanged"/> for why the exception is not
    /// forwarded to the caller.
    /// </remarks>
    private void Raise(WorkspaceChangedEventArgs notification)
    {
        EventHandler<WorkspaceChangedEventArgs>? handler = SnapshotChanged;

        if (handler is null)
        {
            return;
        }

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<WorkspaceChangedEventArgs>)subscriber)(this, notification);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception)
#pragma warning restore CA1031
            {
                // Deliberately swallowed, and deliberately every type including
                // OperationCanceledException: the guarantee is that one subscriber cannot affect
                // another, and an exception type that escaped would be a hole in it. A subscriber's
                // bug belongs to the subscriber, and the caller of a load that succeeded is the
                // wrong party to hand it to.
            }
        }
    }
}
