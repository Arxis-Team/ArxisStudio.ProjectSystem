using System;
using Avalonia.Controls;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>
/// A control that subscribes to something the process keeps for its whole life, and lets go only
/// when told.
/// </summary>
/// <remarks>
/// The ordinary way a user's control keeps its generation in the process however carefully the
/// designer lets go: the process's event holds the handler, the handler the control, the control its
/// type, and the type the whole collectible context. A reclaim has to answer "still held" for it.
/// </remarks>
public sealed class SubscribingControl : Control
{
    /// <summary>Creates the control and subscribes it.</summary>
    public SubscribingControl() => AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

    /// <summary>Lets go of the subscription, which is what lets the generation go.</summary>
    public void Unsubscribe() => AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

    private void OnProcessExit(object? sender, EventArgs e) => IsVisible = false;
}

/// <summary>
/// A window that refuses to close until it is allowed to.
/// </summary>
/// <remarks>
/// The windowing platform keeps every window that has not closed, so a window the designer cannot
/// close keeps its generation however it lets go of everything else.
/// </remarks>
public sealed class StubbornWindow : Window
{
    /// <summary>Gets or sets a value indicating whether a close goes through.</summary>
    public bool AllowClose { get; set; }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        e.Cancel = !AllowClose;

        base.OnClosing(e);
    }
}
