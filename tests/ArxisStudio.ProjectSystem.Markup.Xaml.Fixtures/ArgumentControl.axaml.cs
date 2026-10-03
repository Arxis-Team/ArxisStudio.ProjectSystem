using Avalonia.Controls;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>
/// A project's own user control that markup cannot create: its one constructor takes what the
/// application gives it, as a view built by dependency injection does.
/// </summary>
public partial class ArgumentControl : UserControl
{
    /// <summary>Creates the control from its compiled markup, with what it was given.</summary>
    /// <param name="caption">What the application gives it.</param>
    public ArgumentControl(string caption)
    {
        InitializeComponent();
        Tag = caption;
    }
}
