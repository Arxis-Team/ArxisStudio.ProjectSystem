using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Gathers file changes as they arrive and delivers them as one batch once they stop.
/// </summary>
/// <remarks>
/// <para>
/// A file system does not report one change per edit. Saving a file in an editor produces two or
/// three notifications; a branch switch produces thousands over several seconds; a build writes its
/// own output back into a directory somebody is watching. Reacting to each one separately would
/// re-evaluate a solution repeatedly to reach the state it would have reached once.
/// </para>
/// <para>
/// So changes accumulate into a distinct set and are delivered when either the changes have stopped
/// for <see cref="FileChangeCoalescingOptions.QuietPeriod"/> or
/// <see cref="FileChangeCoalescingOptions.MaximumDelay"/> has passed since the first of them —
/// whichever comes first.
/// </para>
/// <para>
/// <b>Time comes from a <see cref="TimeProvider"/></b>, which is the whole reason this is testable.
/// The contract forbids using delays as a substitute for coordination, and a debouncer is the one
/// thing here that genuinely depends on the passage of time; taking the clock as a parameter means a
/// test advances it by hand and every assertion is exact rather than likely.
/// </para>
/// <para>
/// <b>This coalesces; it does not decide anything.</b> What a batch means is
/// <see cref="SolutionSnapshot.Invalidate"/>'s question, and keeping the two apart is what lets both
/// be tested without the other.
/// </para>
/// </remarks>
public sealed class FileChangeCoalescer : IDisposable
{
    private readonly Action<ImmutableArray<CanonicalPath>> _onBatch;
    private readonly FileChangeCoalescingOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _sync = new();

    private readonly HashSet<CanonicalPath> _seen = [];
    private readonly List<CanonicalPath> _pending = [];

    private long _firstChangeAt;
    private bool _disposed;

    /// <summary>Creates a coalescer.</summary>
    /// <param name="onBatch">
    /// Called with each batch, on a timer thread, never with an empty batch and never for two
    /// batches at once. An exception it throws is swallowed — see the remarks on
    /// <see cref="Flush"/>.
    /// </param>
    /// <param name="options">How long to wait, or <see langword="null"/> for the defaults.</param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for the system one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="onBatch"/> is <see langword="null"/>.</exception>
    public FileChangeCoalescer(
        Action<ImmutableArray<CanonicalPath>> onBatch,
        FileChangeCoalescingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onBatch);

        _onBatch = onBatch;
        _options = options ?? FileChangeCoalescingOptions.Default;
        _time = timeProvider ?? TimeProvider.System;

        // Created idle. Timer.Change is what schedules it, so there is exactly one timer for the
        // lifetime of this object rather than one per burst.
        _timer = _time.CreateTimer(static state => ((FileChangeCoalescer)state!).Deliver(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Records a change, and starts or extends the wait.</summary>
    /// <remarks>
    /// Safe to call from any thread, which it has to be: a file watcher reports on threads of its
    /// own choosing and several of them may report at once. An empty path is ignored, and the same
    /// path arriving repeatedly is recorded once.
    /// </remarks>
    /// <param name="path">The file that changed.</param>
    public void Add(CanonicalPath path)
    {
        if (path.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_seen.Add(path))
            {
                _pending.Add(path);
            }

            if (_pending.Count == 1)
            {
                _firstChangeAt = _time.GetTimestamp();
            }

            _timer.Change(NextDelay(), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Delivers whatever is pending now, without waiting.
    /// </summary>
    /// <remarks>
    /// For a host that knows the burst is over — a save-all completing, a build finishing — and for
    /// shutting down without losing what had accumulated. Does nothing when nothing is pending, so
    /// it is safe to call unconditionally.
    /// </remarks>
    public void Flush() => Deliver();

    /// <summary>Stops the timer. Anything still pending is dropped.</summary>
    /// <remarks>
    /// Dropped rather than delivered, because delivering during disposal would call arbitrary code
    /// at the moment the caller said it was finished. A caller that wants the last batch calls
    /// <see cref="Flush"/> first, which is why that method is public.
    /// </remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
            _seen.Clear();
        }

        _timer.Dispose();
    }

    /// <summary>
    /// How long to wait from now: the quiet period, or whatever is left of the ceiling if that is
    /// sooner. Never negative, because a ceiling already passed means fire immediately.
    /// </summary>
    private TimeSpan NextDelay()
    {
        TimeSpan elapsed = _time.GetElapsedTime(_firstChangeAt);
        TimeSpan untilCeiling = _options.MaximumDelay - elapsed;

        TimeSpan delay = untilCeiling < _options.QuietPeriod ? untilCeiling : _options.QuietPeriod;

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private void Deliver()
    {
        ImmutableArray<CanonicalPath> batch;

        lock (_sync)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];

            _pending.Clear();
            _seen.Clear();

            // Nothing is pending now, so no wake-up is wanted. Without this a Flush would leave the
            // timer armed to fire on an empty batch.
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // Outside the lock, for the same reason the workspace raises its events outside the gate:
        // this is arbitrary code, and it is entitled to call back in.
        try
        {
            _onBatch(batch);
        }
#pragma warning disable CA1031 // A batch handler throwing on a timer thread would take the process
        catch (Exception)      // down. Isolated, exactly as a snapshot subscriber is -- ADR 0007.
#pragma warning restore CA1031
        {
        }
    }
}
