using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// A provider a test can stop inside and start again when it chooses.
/// </summary>
/// <remarks>
/// <para>
/// Every concurrency guarantee worth testing here is about what happens <em>while</em> a load is in
/// flight, and the only honest way to test that is to hold the window open rather than to guess how
/// long it lasts. So a load parks on a <see cref="TaskCompletionSource"/> the moment it starts, the
/// test is told it arrived, and nothing moves until the test says so.
/// </para>
/// <para>
/// This is why there is no <c>Thread.Sleep</c> and no <c>Task.Delay</c> anywhere in these tests.
/// Those would make the assertions likely rather than certain, and a probabilistic concurrency test
/// is a test that fails on somebody else's machine.
/// </para>
/// </remarks>
internal sealed class ControllableProvider : IProjectSystemProvider
{
    private readonly Lock _sync = new();
    private readonly Queue<ControllableLoad> _arrived = new();
    private readonly Queue<TaskCompletionSource<ControllableLoad>> _watchers = new();

    private int _started;

    /// <summary>Gets the provider's name.</summary>
    public string Name { get; init; } = "Controllable";

    /// <summary>Gets or sets whether this provider claims entry points. Read by <see cref="CanLoad"/>.</summary>
    public bool Accepts { get; set; } = true;

    /// <summary>Gets or sets whether <see cref="CanLoad"/> throws instead of answering.</summary>
    public bool CanLoadThrows { get; set; }

    /// <summary>
    /// Gets or sets whether a parked load observes its cancellation token.
    /// </summary>
    /// <remarks>
    /// Set to <see langword="false"/> to model a provider that ignores cancellation and returns a
    /// perfectly good snapshot anyway. The workspace must still refuse to publish it.
    /// </remarks>
    public bool ObservesCancellation { get; set; } = true;

    /// <summary>Gets how many loads have started.</summary>
    public int Started => Volatile.Read(ref _started);

    /// <summary>Completes when the next load enters this provider and parks.</summary>
    /// <returns>The parked load, which the test then completes however it likes.</returns>
    public Task<ControllableLoad> NextArrivalAsync()
    {
        lock (_sync)
        {
            if (_arrived.Count > 0)
            {
                return Task.FromResult(_arrived.Dequeue());
            }

            var watcher = new TaskCompletionSource<ControllableLoad>(TaskCreationOptions.RunContinuationsAsynchronously);

            _watchers.Enqueue(watcher);

            return watcher.Task;
        }
    }

    /// <inheritdoc />
    public bool CanLoad(WorkspaceEntryPoint entryPoint) =>
        CanLoadThrows
            ? throw new InvalidOperationException("This provider is broken and cannot answer.")
            : Accepts;

    /// <inheritdoc />
    public async ValueTask<WorkspaceLoadResult> LoadAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken)
    {
        var load = new ControllableLoad(
            request, Interlocked.Increment(ref _started), ObservesCancellation, cancellationToken);

        TaskCompletionSource<ControllableLoad>? watcher = null;

        lock (_sync)
        {
            if (_watchers.Count > 0)
            {
                watcher = _watchers.Dequeue();
            }
            else
            {
                _arrived.Enqueue(load);
            }
        }

        watcher?.TrySetResult(load);

        return await load.WaitAsync().ConfigureAwait(false);
    }
}

/// <summary>One load, parked inside the provider until a test decides how it ends.</summary>
internal sealed class ControllableLoad(
    WorkspaceLoadRequest request,
    int ordinal,
    bool observesCancellation,
    CancellationToken cancellationToken)
{
    private readonly TaskCompletionSource<WorkspaceLoadResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets which load this is, counting from one.</summary>
    public int Ordinal { get; } = ordinal;

    /// <summary>Gets the request the workspace passed in.</summary>
    public WorkspaceLoadRequest Request { get; } = request;

    /// <summary>Gets the token the workspace passed in.</summary>
    public CancellationToken Token { get; } = cancellationToken;

    /// <summary>Finishes this load with a snapshot.</summary>
    public void CompleteWith(SolutionSnapshot snapshot) =>
        _completion.TrySetResult(WorkspaceLoadResult.Success(snapshot));

    /// <summary>Finishes this load with a result built by the caller.</summary>
    public void CompleteWith(WorkspaceLoadResult result) => _completion.TrySetResult(result);

    /// <summary>Finishes this load with an error diagnostic and no snapshot.</summary>
    public void Fail(string message = "the provider could not load this") =>
        _completion.TrySetResult(WorkspaceLoadResult.Failure(
            new ProjectDiagnostic("APS1002", message, ProjectDiagnosticSeverity.Error)));

    /// <summary>Finishes this load by throwing, as a provider with a bug would.</summary>
    public void Throw(Exception exception) => _completion.TrySetException(exception);

    /// <summary>Waits for the test to decide, observing the token unless told not to.</summary>
    internal Task<WorkspaceLoadResult> WaitAsync() =>
        observesCancellation ? _completion.Task.WaitAsync(Token) : _completion.Task;
}
