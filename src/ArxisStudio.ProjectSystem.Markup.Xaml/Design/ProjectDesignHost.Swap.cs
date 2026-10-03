using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Replacing a generation: in order, proven, and only when nothing holds it off.</summary>
public sealed partial class ProjectDesignHost
{
    private Task? _swap;

    /// <summary>Replaces the generation now, whether or not it is stale, and whatever holds the swap off.</summary>
    /// <remarks>
    /// <para>
    /// The order is the one a swap has to keep (ADR 0028): the participants let go, the documents are
    /// detached and the windows their sessions built are closed, the host lets go of the population and
    /// the environments, the designer's own state goes last
    /// (<see cref="ProjectDesignHostOptions.ReleaseHostState"/>), the dispatcher finishes its frame — and
    /// only then is the generation asked to go (<see cref="ProjectAssemblyContext.TryReclaimAsync"/>).
    /// When it went, the successor is built and loaded, the documents somebody is looking at are attached
    /// to it, and the participants take it up. When it did not, there is no successor:
    /// <see cref="RestartRequired"/> is raised and the documents stay detached, their text and history
    /// whole.
    /// </para>
    /// <para>
    /// Once the host requires a restart there is nothing to swap: a generation found held is not asked
    /// again, and a package that moved stays loaded. The report says the generation was not reclaimed,
    /// and nothing is touched.
    /// </para>
    /// </remarks>
    /// <param name="reason">Why, for whoever reads the report.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What the swap did, phase by phase.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/>, empty or blank.</exception>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async ValueTask<ProjectDesignSwapReport> SwapAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ThrowIfDisposed();

        lock (_sync)
        {
            if (!_started)
            {
                throw new InvalidOperationException("Start the design host before swapping its generation.");
            }
        }

        return (await SwapCoreAsync(reason, automatic: false, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>The swap the host started on its own, or a completed task when none is in flight. For tests.</summary>
    internal Task SwapInFlight
    {
        get
        {
            lock (_sync)
            {
                return _swap ?? Task.CompletedTask;
            }
        }
    }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        if (Gate.IsOpen)
        {
            TrySwapSoon();
        }
    }

    /// <summary>Starts a swap when the generation is stale and nothing — no deferral, no build — holds it off.</summary>
    private void TrySwapSoon()
    {
        lock (_sync)
        {
            if (!CanSwapNow() || _swap is { IsCompleted: false })
            {
                return;
            }

            _swap = Task.Run(() => SwapAutomaticallyAsync(_shutdown.Token), CancellationToken.None);
        }
    }

    /// <summary>Whether an automatic swap may run now. Under the lock.</summary>
    private bool CanSwapNow() =>
        Volatile.Read(ref _disposed) == 0
        && _started
        && _stale
        && _restartReason is null
        && !_swapping
        && _pendingBuild.Count == 0
        && !_buildArmed
        && !_buildLoopRunning
        && _building.CurrentCount > 0
        && Gate.IsOpen;

    private async Task SwapAutomaticallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SwapCoreAsync(reason: null, automatic: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ReportFailureAsync("replacing the project's types", exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the turn and the build, checks again that the swap is wanted, replaces the generation, and
    /// says so once the host is out of the swap.
    /// </summary>
    /// <returns>The report, or <see langword="null"/> when an automatic swap found it was not wanted any more.</returns>
    private async Task<ProjectDesignSwapReport?> SwapCoreAsync(string? reason, bool automatic, CancellationToken cancellationToken)
    {
        (ProjectDesignSwapReport? report, bool swapped) = await TakeTurnAndSwapAsync(reason, automatic, cancellationToken).ConfigureAwait(false);

        await ReportStateAsync().ConfigureAwait(false);

        if (swapped && report is not null)
        {
            await RaiseAsync(SwapCompleted, new ProjectDesignSwapCompletedEventArgs(report)).ConfigureAwait(false);
        }

        return report;
    }

    /// <returns>The report, and whether a swap ran — an answer without one is the host requiring a restart.</returns>
    private async Task<(ProjectDesignSwapReport? Report, bool Swapped)> TakeTurnAndSwapAsync(
        string? reason, bool automatic, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _building.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                string why;
                string generation;

                lock (_sync)
                {
                    // A deferral taken, or code saved, since the swap was started: whatever lets go of
                    // the swap last starts it again.
                    if (automatic && !(_stale && _restartReason is null && Gate.IsOpen && _pendingBuild.Count == 0 && !_buildArmed))
                    {
                        return (null, false);
                    }

                    why = reason ?? _staleReason ?? "the project's types changed";
                    generation = _generation?.Name ?? "(none)";

                    if (_restartReason is not null)
                    {
                        return (new ProjectDesignSwapReport(why, generation, null, false, default, default, default, default), false);
                    }

                    _swapping = true;
                }

                await ReportStateAsync().ConfigureAwait(false);

                try
                {
                    return (await ReplaceAsync(why, generation, cancellationToken).ConfigureAwait(false), true);
                }
                finally
                {
                    lock (_sync)
                    {
                        _swapping = false;
                    }
                }
            }
            finally
            {
                _building.Release();
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Replaces the generation in the order that lets it go. Inside the turn, holding the build.</summary>
    private async Task<ProjectDesignSwapReport> ReplaceAsync(string reason, string generation, CancellationToken cancellationToken)
    {
        TimeProvider clock = _options.TimeProvider;
        long started = clock.GetTimestamp();

        // The designer lets go: what it shows, then the documents' sessions, then the windows they built.
        await ReleaseParticipantsAsync(cancellationToken).ConfigureAwait(false);
        await DetachDocumentsAsync(cancellationToken).ConfigureAwait(false);
        await CloseApplicationsAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(CloseRetiredRoots).GetTask().ConfigureAwait(false);

        TimeSpan release = clock.GetElapsedTime(started);
        long tornDown = clock.GetTimestamp();

        // The host lets go, and the designer's own state goes last: letting go of the rest moves focus
        // and routes commands, which is what puts a stale element back into it.
        ProjectAssemblyContext? retired = LetGoOfTheGeneration();

        if (_options.ReleaseHostState is { } releaseHostState)
        {
            await Dispatcher.UIThread.InvokeAsync(() => releaseHostState(cancellationToken).AsTask()).ConfigureAwait(false);
        }

        // One turn of the dispatcher, so layout and render work queued for the trees that went lets go.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken).GetTask().ConfigureAwait(false);

        TimeSpan teardown = clock.GetElapsedTime(tornDown);
        long reclaiming = clock.GetTimestamp();

        bool reclaimed = retired is null || await retired.TryReclaimAsync(cancellationToken).ConfigureAwait(false);

        TimeSpan reclaim = clock.GetElapsedTime(reclaiming);

        if (!reclaimed)
        {
            var held = new ProjectDesignSwapReport(reason, generation, null, false, release, teardown, reclaim, default);

            await RequireRestartAsync(
                ProjectDesignRestartReason.GenerationStillHeld,
                "The project's previous types are still held in this process, so the new ones cannot be loaded beside them. "
                    + "Only a new process shows the types as they are now.").ConfigureAwait(false);

            return held;
        }

        long rebuilding = clock.GetTimestamp();

        // The successor: what is out of date built first, then loaded, then shown and taken up — unless
        // another generation of the same assemblies stays in the process, or the design set is built against
        // an Avalonia this process does not run, and there is none.
        if (_source.Snapshot is { } successor && AvaloniaFits(successor, DesignSetOf(successor)))
        {
            await BuildWhatIsOutOfDateAsync("the types are being replaced", cancellationToken).ConfigureAwait(false);
            await CreateGenerationAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_source.Snapshot is { } unsupported)
        {
            NoGeneration(unsupported);
        }

        if (GenerationName is not null)
        {
            await AttachVisibleDocumentsAsync(cancellationToken).ConfigureAwait(false);
            await RestoreParticipantsAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ProjectDesignSwapReport(
            reason, generation, GenerationName, true, release, teardown, reclaim, clock.GetElapsedTime(rebuilding));
    }

    /// <summary>Asks every participant to let go, each on the user interface thread, in the order they registered.</summary>
    /// <remarks>
    /// Its own method, so that an exception a participant threw — reported, and not stopping the others —
    /// dies with this method's state rather than living on in the swap's while the generation is asked to go.
    /// </remarks>
    private async Task ReleaseParticipantsAsync(CancellationToken cancellationToken)
    {
        foreach (IProjectDesignParticipant participant in Participants())
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => participant.ReleaseAsync(cancellationToken).AsTask()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ReportFailureAsync("a part of the designer letting go of the project's types", exception).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Asks every participant to take up the successor, each on the user interface thread.</summary>
    private async Task RestoreParticipantsAsync(CancellationToken cancellationToken)
    {
        foreach (IProjectDesignParticipant participant in Participants())
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => participant.RestoreAsync(cancellationToken).AsTask()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ReportFailureAsync("a part of the designer taking up the project's types", exception).ConfigureAwait(false);
            }
        }
    }

    private IProjectDesignParticipant[] Participants()
    {
        lock (_sync)
        {
            return [.. _participants];
        }
    }

    /// <summary>Records that only a new process shows the project as it is now, and says so once.</summary>
    private async Task RequireRestartAsync(ProjectDesignRestartReason reason, string message)
    {
        lock (_sync)
        {
            if (_restartReason is not null)
            {
                return;
            }

            _restartReason = reason;
        }

        await RaiseAsync(RestartRequired, new ProjectDesignRestartEventArgs(reason, message)).ConfigureAwait(false);
        await ReportStateAsync().ConfigureAwait(false);
    }
}
