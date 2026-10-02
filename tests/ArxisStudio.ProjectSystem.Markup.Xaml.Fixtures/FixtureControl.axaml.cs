using Avalonia.Controls;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>A project's own user control: compiled markup behind an <c>x:Class</c>.</summary>
public partial class FixtureControl : UserControl
{
    /// <summary>Creates the control from its compiled markup.</summary>
    public FixtureControl()
    {
        InitializeComponent();
    }
}
