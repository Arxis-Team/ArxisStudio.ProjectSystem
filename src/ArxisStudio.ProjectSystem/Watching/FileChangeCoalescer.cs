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
/// So changes accumulate and are delivered when either the changes have stopped for
/// <see cref="FileChangeCoalescingOptions.QuietPeriod"/> or <see cref="FileChangeCoalescingOptions.MaximumDelay"/>
/// has passed since the first of them — whichever comes first.
/// </para>
/// <para>
/// <b>A batch says what is true at its end, path by path</b>
/// (<see href="../../docs/adr/0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md">ADR 0025</see>).
/// A file created and deleted inside it never happened; a file deleted and created again was
/// replaced, which is a change; a file renamed into a path that had just been renamed away is the
/// same file with new contents. That last is how JetBrains Rider and every atomic writer save — the
/// text to a temporary file, the original renamed aside, the temporary renamed over it, the
/// original deleted — and it arrives as one <see cref="FileChangeKind.Changed"/> instead of five
/// events that each look like the project gaining or losing a file. A rename whose two ends are
/// still a disappearance and an appearance at the end of the batch stays a rename, and renames in a
/// chain are one rename from the first path to the last.
/// </para>
/// <para>
/// <b>Time comes from a <see cref="TimeProvider"/></b>, which is the whole reason this is testable.
/// The contract forbids using delays as a substitute for coordination, and a debouncer is the one
/// thing here that genuinely depends on the passage of time; taking the clock as a parameter means a
/// test advances it by hand and every assertion is exact rather than likely.
/// </para>
/// <para>
/// <b>This coalesces; it does not decide anything.</b> What a batch means is
/// <see cref="SolutionSnapshot.Classify"/>'s question — or <see cref="SolutionSnapshot.Invalidate"/>'s, for a
/// host that only re-evaluates — and keeping the two apart is what lets both be tested without the
/// other.
/// </para>
/// </remarks>
public sealed class FileChangeCoalescer : IDisposable
{
    private readonly Action<ImmutableArray<CanonicalPath>>? _onPaths;
    private readonly Action<ImmutableArray<FileChange>>? _onChanges;
    private readonly FileChangeCoalescingOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _sync = new();

    // Held while a batch is handed over, and only then. A second delivery waits for the first to
    // return rather than running beside it. A handler that calls Flush itself re-enters on its own
    // thread, which Lock permits, instead of waiting for itself.
    private readonly Lock _delivery = new();

    /// <summary>What each path amounts to so far in this batch.</summary>
    private readonly Dictionary<CanonicalPath, FileChangeKind> _net = [];

    /// <summary>Every path the batch has heard of, in the order it first heard of it.</summary>
    private readonly List<CanonicalPath> _order = [];
    private readonly HashSet<CanonicalPath> _ordered = [];

    /// <summary>The renames of this batch, from where a file first was to where it last went.</summary>
    private readonly List<(CanonicalPath Old, CanonicalPath New)> _renames = [];

    private bool _overflow;
    private bool _open;
    private long _firstChangeAt;
    private bool _disposed;

    /// <summary>Creates a coalescer that delivers the paths that changed.</summary>
    /// <param name="onBatch">
    /// Called with each batch — on a timer thread, or on the thread that called <see cref="Flush"/> —
    /// never with an empty batch and never for two batches at once: a delivery that finds one in
    /// progress waits for it. A rename delivers both of its paths; a lost change cannot be said as a
    /// path and is left out. An exception it throws is swallowed — see the remarks on
    /// <see cref="Flush"/>.
    /// </param>
    /// <param name="options">How long to wait, or <see langword="null"/> for the defaults.</param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for the system one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="onBatch"/> is <see langword="null"/>.</exception>
    public FileChangeCoalescer(
        Action<ImmutableArray<CanonicalPath>> onBatch,
        FileChangeCoalescingOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(options, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(onBatch);

        _onPaths = onBatch;
    }

    private FileChangeCoalescer(Action<ImmutableArray<FileChange>> onChanges, FileChangeCoalescingOptions? options, TimeProvider? timeProvider)
        : this(options, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(onChanges);

        _onChanges = onChanges;
    }

    private FileChangeCoalescer(FileChangeCoalescingOptions? options, TimeProvider? timeProvider)
    {
        _options = options ?? FileChangeCoalescingOptions.Default;
        _time = timeProvider ?? TimeProvider.System;

        // Created idle. Timer.Change is what schedules it, so there is exactly one timer for the
        // lifetime of this object rather than one per burst.
        _timer = _time.CreateTimer(static state => ((FileChangeCoalescer)state!).Deliver(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Creates a coalescer that delivers what happened to each file.</summary>
    /// <remarks>
    /// A factory rather than a second constructor: a constructor taking a delegate of a different
    /// type would make every call that passes a lambda or <see langword="null"/> ambiguous between the two.
    /// </remarks>
    /// <param name="onChanges">
    /// Called with each batch, under the same rules as the path batches of the constructor: never
    /// empty, never two at once, exceptions swallowed. A lost change comes first, as
    /// <see cref="FileChange.Overflow"/>.
    /// </param>
    /// <param name="options">How long to wait, or <see langword="null"/> for the defaults.</param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for the system one.</param>
    /// <returns>The coalescer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="onChanges"/> is <see langword="null"/>.</exception>
    public static FileChangeCoalescer ForChanges(
        Action<ImmutableArray<FileChange>> onChanges,
        FileChangeCoalescingOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(onChanges, options, timeProvider);

    /// <summary>Records a change to a file, and starts or extends the wait.</summary>
    /// <remarks>
    /// The same as <see cref="Add(FileChange)"/> with <see cref="FileChangeKind.Changed"/>: what a
    /// watcher that reports bare paths knows.
    /// </remarks>
    /// <param name="path">The file that changed. An empty path is ignored.</param>
    public void Add(CanonicalPath path) => Add(new FileChange(path, FileChangeKind.Changed));

    /// <summary>Records a change, and starts or extends the wait.</summary>
    /// <remarks>
    /// Safe to call from any thread, which it has to be: a file watcher reports on threads of its
    /// own choosing and several of them may report at once. A rename missing one of its paths is the
    /// end it has — an arrival or a departure — and a change with no path at all, other than
    /// <see cref="FileChange.Overflow"/>, is ignored.
    /// </remarks>
    /// <param name="change">What happened.</param>
    public void Add(FileChange change)
    {
        if (change.Kind == FileChangeKind.Renamed && (change.OldPath.IsEmpty || change.Path.IsEmpty))
        {
            change = change.OldPath.IsEmpty
                ? new FileChange(change.Path, FileChangeKind.Created)
                : new FileChange(change.OldPath, FileChangeKind.Deleted);
        }

        if (change.Kind != FileChangeKind.Overflow && change.Path.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            switch (change.Kind)
            {
                case FileChangeKind.Overflow:
                    _overflow = true;
                    break;

                case FileChangeKind.Renamed:
                    Apply(change.OldPath, FileChangeKind.Deleted);
                    Apply(change.Path, FileChangeKind.Created);
                    Chain(change.OldPath, change.Path);
                    break;

                default:
                    Apply(change.Path, change.Kind);
                    break;
            }

            // The ceiling counts from the first change of the batch. A repeat of a path already
            // pending is not a first change however recently it arrived, and restarting the count
            // on one let a single busy file postpone its own batch for as long as it kept changing.
            if (!_open)
            {
                _open = true;
                _firstChangeAt = _time.GetTimestamp();
            }

            _timer.Change(NextDelay(), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Delivers whatever is pending now, without waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a host that knows the burst is over — a save-all completing, a build finishing — and for
    /// shutting down without losing what had accumulated. Does nothing when nothing is pending, so
    /// it is safe to call unconditionally.
    /// </para>
    /// <para>
    /// "Without waiting" is for the timer, not for a batch already being handled: that one is
    /// finished first, so what this delivers always comes after it. A handler calling this from
    /// inside its own delivery is the exception, and receives the next batch at once on its own
    /// thread rather than waiting for itself.
    /// </para>
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
            Reset();
        }

        _timer.Dispose();
    }

    /// <summary>
    /// Folds one event into what its path amounts to so far.
    /// </summary>
    /// <remarks>
    /// Only the first and the last state matter to anybody reading the batch: a path that did not
    /// exist before and does not exist after was never there; one that existed before and exists
    /// after changed, whatever happened between.
    /// </remarks>
    private void Apply(CanonicalPath path, FileChangeKind kind)
    {
        if (_ordered.Add(path))
        {
            _order.Add(path);
        }

        if (!_net.TryGetValue(path, out FileChangeKind current))
        {
            _net[path] = kind;
            return;
        }

        FileChangeKind? next = (current, kind) switch
        {
            // Appeared in this batch: still new, or never there at all.
            (FileChangeKind.Created, FileChangeKind.Deleted) => null,
            (FileChangeKind.Created, _) => FileChangeKind.Created,

            // Gone in this batch and back: replaced, which is a change to the file that was there.
            (FileChangeKind.Deleted, FileChangeKind.Deleted) => FileChangeKind.Deleted,
            (FileChangeKind.Deleted, _) => FileChangeKind.Changed,

            // Was there before the batch.
            (FileChangeKind.Changed, FileChangeKind.Deleted) => FileChangeKind.Deleted,
            _ => FileChangeKind.Changed,
        };

        if (next is { } kept)
        {
            _net[path] = kept;
        }
        else
        {
            _net.Remove(path);
        }
    }

    /// <summary>
    /// Records a rename, joining it to one that brought the file to its old path in this batch.
    /// </summary>
    private void Chain(CanonicalPath oldPath, CanonicalPath newPath)
    {
        for (int i = 0; i < _renames.Count; i++)
        {
            if (_renames[i].New == oldPath)
            {
                _renames[i] = (_renames[i].Old, newPath);
                return;
            }
        }

        _renames.Add((oldPath, newPath));
    }

    /// <summary>
    /// What the batch amounts to: a lost change first, then each path in the order it was first
    /// heard of, a rename at the place of its old path.
    /// </summary>
    private ImmutableArray<FileChange> Changes()
    {
        ImmutableArray<FileChange>.Builder changes = ImmutableArray.CreateBuilder<FileChange>();

        if (_overflow)
        {
            changes.Add(FileChange.Overflow);
        }

        // A rename stays one only where its ends are still an absence and an arrival: a file renamed
        // away and another written in its place is a change to that path, not a move.
        var moved = new Dictionary<CanonicalPath, CanonicalPath>();
        var arrived = new HashSet<CanonicalPath>();

        foreach ((CanonicalPath oldPath, CanonicalPath newPath) in _renames)
        {
            if (_net.GetValueOrDefault(oldPath, FileChangeKind.Changed) == FileChangeKind.Deleted
                && _net.TryGetValue(newPath, out FileChangeKind landed)
                && landed == FileChangeKind.Created
                && !arrived.Contains(newPath))
            {
                moved[oldPath] = newPath;
                arrived.Add(newPath);
            }
        }

        foreach (CanonicalPath path in _order)
        {
            if (moved.TryGetValue(path, out CanonicalPath to))
            {
                changes.Add(FileChange.Renamed(path, to));
            }
            else if (!arrived.Contains(path) && _net.TryGetValue(path, out FileChangeKind kind))
            {
                changes.Add(new FileChange(path, kind));
            }
        }

        return changes.ToImmutable();
    }

    /// <summary>The paths a batch of changes names, each once — what a path batch delivers.</summary>
    private static ImmutableArray<CanonicalPath> Paths(ImmutableArray<FileChange> changes)
    {
        var seen = new HashSet<CanonicalPath>();
        ImmutableArray<CanonicalPath>.Builder paths = ImmutableArray.CreateBuilder<CanonicalPath>();

        foreach (FileChange change in changes)
        {
            if (change.Kind == FileChangeKind.Renamed && seen.Add(change.OldPath))
            {
                paths.Add(change.OldPath);
            }

            if (!change.Path.IsEmpty && seen.Add(change.Path))
            {
                paths.Add(change.Path);
            }
        }

        return paths.ToImmutable();
    }

    private void Reset()
    {
        _net.Clear();
        _order.Clear();
        _ordered.Clear();
        _renames.Clear();
        _overflow = false;
        _open = false;
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
        // One batch at a time, in the order they were taken. Without this, a timer firing while a
        // slow handler was still running, or a Flush racing the timer, handed the next batch over
        // on a second thread while the first was still being handled. The state lock is taken
        // inside this one and never the other way round, so the two cannot wait on each other.
        lock (_delivery)
        {
            ImmutableArray<FileChange> changes;

            lock (_sync)
            {
                if (_disposed || !_open)
                {
                    return;
                }

                changes = Changes();

                Reset();

                // Nothing is pending now, so no wake-up is wanted. Without this a Flush would leave
                // the timer armed to fire on an empty batch.
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            // Outside the state lock, for the same reason the workspace raises its events outside
            // the gate: this is arbitrary code, and it is entitled to call back in.
            try
            {
                if (_onChanges is not null)
                {
                    if (!changes.IsEmpty)
                    {
                        _onChanges(changes);
                    }
                }
                else if (Paths(changes) is { IsEmpty: false } paths)
                {
                    _onPaths!(paths);
                }
            }
#pragma warning disable CA1031 // A batch handler throwing on a timer thread would take the process
            catch (Exception)      // down. Isolated, exactly as a snapshot subscriber is -- ADR 0007.
#pragma warning restore CA1031
            {
            }
        }
    }
}
