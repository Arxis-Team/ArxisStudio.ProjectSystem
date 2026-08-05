using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Gathering a burst of file changes into one batch.
/// </summary>
/// <remarks>
/// Every case advances a fake clock by an exact amount, so "the quiet period has not elapsed" is a
/// fact rather than a hope. Nothing here sleeps, delays, or retries.
/// </remarks>
public sealed class CoalescingTests
{
    private static CanonicalPath A => TestPaths.At("src", "a.props");

    private static CanonicalPath B => TestPaths.At("src", "b.props");

    private static FileChangeCoalescingOptions Options { get; } = new()
    {
        QuietPeriod = TimeSpan.FromMilliseconds(250),
        MaximumDelay = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void BeforeTheQuietPeriodElapses_NothingIsDelivered()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            time.Advance(TimeSpan.FromMilliseconds(249));

            Assert.Empty(batches);
        }
    }

    [Fact]
    public void OnceTheChangesStop_TheBatchArrives()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            time.Advance(TimeSpan.FromMilliseconds(250));

            Assert.Equal([A], Assert.Single(batches));
        }
    }

    /// <summary>
    /// Each change restarts the wait, which is what "debounce" means and what makes a save that
    /// produces three notifications produce one refresh.
    /// </summary>
    [Fact]
    public void AChangeDuringTheQuietPeriod_ExtendsIt()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            time.Advance(TimeSpan.FromMilliseconds(200));

            coalescer.Add(B);
            time.Advance(TimeSpan.FromMilliseconds(200));

            Assert.Empty(batches);

            time.Advance(TimeSpan.FromMilliseconds(50));

            Assert.Equal([A, B], Assert.Single(batches));
        }
    }

    /// <summary>
    /// The case a quiet period alone never handles. A branch switch writes files continuously for
    /// seconds, so something has to fire while it is still going on.
    /// </summary>
    [Fact]
    public void AStreamOfChangesThatNeverStops_IsDeliveredAtTheCeiling()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            for (int tick = 0; tick < 20; tick++)
            {
                coalescer.Add(TestPaths.At("src", $"file{tick}.props"));
                time.Advance(TimeSpan.FromMilliseconds(100));
            }

            ImmutableArray<CanonicalPath> batch = Assert.Single(batches);

            // Two seconds at a change every 100ms: everything up to the ceiling, and nothing after
            // it, because the changes that arrive later belong to the next batch.
            Assert.Equal(20, batch.Length);
        }
    }

    [Fact]
    public void TheSameFileChangingRepeatedly_IsOneEntry()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            coalescer.Add(A);
            coalescer.Add(A);
            time.Advance(Options.QuietPeriod);

            Assert.Equal([A], Assert.Single(batches));
        }
    }

    [Fact]
    public void AnEmptyPath_IsIgnored()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(CanonicalPath.None);
            time.Advance(Options.MaximumDelay);

            Assert.Empty(batches);
        }
    }

    /// <summary>A batch delivered is a batch forgotten; the next burst starts from nothing.</summary>
    [Fact]
    public void AfterABatch_TheNextOneStartsEmpty()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            time.Advance(Options.QuietPeriod);

            coalescer.Add(B);
            time.Advance(Options.QuietPeriod);

            Assert.Equal(2, batches.Count);
            Assert.Equal([A], batches[0]);
            Assert.Equal([B], batches[1]);
        }
    }

    [Fact]
    public void Flush_DeliversWithoutWaiting()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        using (coalescer)
        {
            coalescer.Add(A);
            coalescer.Flush();

            Assert.Equal([A], Assert.Single(batches));

            // And the timer it had armed does not then fire on an empty batch.
            time.Advance(Options.MaximumDelay);

            Assert.Single(batches);
        }
    }

    [Fact]
    public void FlushWithNothingPending_DeliversNothing()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, _) = Create();

        using (coalescer)
        {
            coalescer.Flush();
            coalescer.Flush();

            Assert.Empty(batches);
        }
    }

    /// <summary>
    /// Disposal drops rather than delivers, because calling arbitrary code at the moment the caller
    /// said it was finished is worse than losing a batch it can ask for with <c>Flush</c>.
    /// </summary>
    [Fact]
    public void AfterDisposal_NothingIsDelivered()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<CanonicalPath>> batches, FakeTimeProvider time) =
            Create();

        coalescer.Add(A);
        coalescer.Dispose();

        time.Advance(Options.MaximumDelay);
        coalescer.Add(B);
        coalescer.Flush();

        Assert.Empty(batches);
    }

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        (FileChangeCoalescer coalescer, _, _) = Create();

        coalescer.Dispose();
        coalescer.Dispose();
    }

    /// <summary>
    /// The handler runs on a timer thread, where an escaping exception ends the process. Isolated
    /// for the same reason a snapshot subscriber is.
    /// </summary>
    [Fact]
    public void AThrowingHandler_DoesNotStopTheNextBatch()
    {
        var delivered = new List<CanonicalPath>();
        var time = new FakeTimeProvider();
        bool first = true;

        using var coalescer = new FileChangeCoalescer(
            batch =>
            {
                if (first)
                {
                    first = false;

                    throw new InvalidOperationException("the subscriber fell over");
                }

                delivered.AddRange(batch);
            },
            Options,
            time);

        coalescer.Add(A);
        time.Advance(Options.QuietPeriod);

        coalescer.Add(B);
        time.Advance(Options.QuietPeriod);

        Assert.Equal([B], delivered);
    }

    [Fact]
    public void ANullHandler_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new FileChangeCoalescer(null!));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeWaits_Throw(int quiet, int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileChangeCoalescingOptions
        {
            QuietPeriod = TimeSpan.FromSeconds(quiet),
            MaximumDelay = TimeSpan.FromSeconds(maximum),
        });
    }

    [Fact]
    public void TheDefaults_AreASaveApartAndAPersonsPatience()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), FileChangeCoalescingOptions.Default.QuietPeriod);
        Assert.Equal(TimeSpan.FromSeconds(2), FileChangeCoalescingOptions.Default.MaximumDelay);
    }

    private static (FileChangeCoalescer Coalescer, List<ImmutableArray<CanonicalPath>> Batches, FakeTimeProvider Time)
        Create()
    {
        var batches = new List<ImmutableArray<CanonicalPath>>();
        var time = new FakeTimeProvider();

        return (new FileChangeCoalescer(batches.Add, Options, time), batches, time);
    }
}
