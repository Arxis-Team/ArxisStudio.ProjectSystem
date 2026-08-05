using System;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// How long to wait before deciding a burst of file changes is over.
/// </summary>
/// <remarks>
/// Both values exist because either one alone misbehaves. A quiet period alone never fires during a
/// branch switch, which writes files continuously for seconds and would reset the timer every time.
/// A maximum delay alone fires on a fixed schedule whether or not anything is still happening, which
/// re-evaluates a project in the middle of a checkout that is about to change it again.
/// </remarks>
public sealed record FileChangeCoalescingOptions
{
    /// <summary>Gets the defaults: a quarter of a second quiet, two seconds at the outside.</summary>
    /// <remarks>
    /// Chosen to be comfortably longer than a text editor's save — which can produce two or three
    /// notifications for one Ctrl+S — and comfortably shorter than a person noticing a delay.
    /// </remarks>
    public static FileChangeCoalescingOptions Default { get; } = new();

    /// <summary>
    /// Gets how long the changes must stop before a batch is delivered. Default a quarter second.
    /// </summary>
    public TimeSpan QuietPeriod
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(QuietPeriod));

            field = value;
        }
    } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets the longest a change may be held before it is delivered regardless. Default two seconds.
    /// </summary>
    /// <remarks>
    /// Measured from the first change in the batch, not from the last, which is what makes it a
    /// ceiling rather than a second quiet period.
    /// </remarks>
    public TimeSpan MaximumDelay
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(MaximumDelay));

            field = value;
        }
    } = TimeSpan.FromSeconds(2);
}
