using Avalonia.Controls;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>A project's own window, placing a project's own control.</summary>
public partial class FixtureWindow : Window
{
    /// <summary>Creates the window from its compiled markup.</summary>
    public FixtureWindow()
    {
        InitializeComponent();
    }
}
