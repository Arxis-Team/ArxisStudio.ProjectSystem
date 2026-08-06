using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia;
using Avalonia.Controls;

namespace FormsDesigner.ViewModels;

/// <summary>
/// A control the toolbox offers, and the markup that creates it.
/// </summary>
/// <remarks>
/// Markup rather than a type, because what gets dropped is a line of a file. A palette holding
/// <see cref="Type"/> would have to render one back into text at the moment of the drop, and would
/// then have to decide what a sensible new Button says — which is exactly what the snippet already
/// says, in the form the file will hold.
/// </remarks>
public sealed record ToolboxEntry(string Group, string Name, string Xaml, double Width, double Height)
{
    public override string ToString() => Name;
}

public sealed partial class DesignerViewModel
{
    /// <summary>
    /// The palette.
    /// </summary>
    /// <remarks>
    /// Avalonia's own controls, and deliberately only those. A toolbox built by reflecting over the
    /// project's assemblies is a real feature and a different one: it needs the project built, it
    /// needs a rule for which types are controls somebody would place, and it needs a sensible
    /// initial markup per type. This palette is the part that demonstrates the drop reaching the
    /// file, which is the thing worth showing.
    /// </remarks>
    public ObservableCollection<ToolboxEntry> Toolbox { get; } = [];

    private void InitialiseToolbox()
    {
        InitialiseInspector();

        Add("Text", "TextBlock", """<TextBlock Text="Text" />""", 100, 20);
        Add("Text", "TextBox", """<TextBox Width="160" />""", 160, 30);
        Add("Text", "Label", """<Label Content="Label" />""", 80, 26);

        Add("Input", "Button", """<Button Content="Button" />""", 90, 32);
        Add("Input", "CheckBox", """<CheckBox Content="Check" />""", 90, 26);
        Add("Input", "RadioButton", """<RadioButton Content="Option" />""", 90, 26);
        Add("Input", "ToggleSwitch", """<ToggleSwitch />""", 90, 32);
        Add("Input", "Slider", """<Slider Width="160" />""", 160, 32);
        Add("Input", "ComboBox", """<ComboBox Width="160" />""", 160, 32);
        Add("Input", "NumericUpDown", """<NumericUpDown Width="140" />""", 140, 32);

        Add("Display", "ProgressBar", """<ProgressBar Width="160" Value="40" />""", 160, 12);
        Add("Display", "Image", """<Image Width="120" Height="90" />""", 120, 90);
        Add("Display", "Border", """<Border Width="160" Height="90" Background="#22FFFFFF" />""", 160, 90);
        Add("Display", "Separator", """<Separator Width="160" />""", 160, 8);

        Add("Layout", "StackPanel", """<StackPanel Width="180" Height="120" />""", 180, 120);
        Add("Layout", "Grid", """<Grid Width="200" Height="140" />""", 200, 140);
        Add("Layout", "Canvas", """<Canvas Width="200" Height="140" />""", 200, 140);
        Add("Layout", "DockPanel", """<DockPanel Width="200" Height="140" />""", 200, 140);
        Add("Layout", "ScrollViewer", """<ScrollViewer Width="200" Height="140" />""", 200, 140);

        void Add(string group, string name, string xaml, double width, double height) =>
            Toolbox.Add(new ToolboxEntry(group, name, xaml, width, height));
    }

    /// <summary>
    /// Drops a control onto a form at a point on that form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent is decided by what is under the pointer rather than by what is selected, which is
    /// what makes dropping into a panel work the way a person expects: hover over the Canvas inside
    /// the form and the control lands in the Canvas, hover over the form itself and it lands there.
    /// </para>
    /// <para>
    /// A position is written only when the parent is one that honours it. Dropping into a
    /// <see cref="StackPanel"/> puts the control at the end and says nothing about coordinates,
    /// because the panel is what decides them — writing <c>Canvas.Left</c> there would produce an
    /// attribute that does nothing and a file that lies about the layout.
    /// </para>
    /// </remarks>
    public void Drop(FormViewModel form, ToolboxEntry entry, Control? over, Point at) =>
        RunDetached(() => DropAsync(form, entry, over, at));

    private async Task DropAsync(FormViewModel form, ToolboxEntry entry, Control? over, Point at)
    {
        if (form.Objects is not { } map || form.Document?.Root is not { } root)
        {
            return;
        }

        (XamlElement parent, Control? parentControl) = ParentFor(map, root, over);

        string xaml = entry.Xaml;

        if (parentControl is Canvas or null || ReferenceEquals(parentControl, form.Root))
        {
            Point local = at;

            xaml = WithPosition(entry, local);
        }

        await ApplyAsync(
            form,
            editor => editor.InsertElement(parent, IndexAtEnd(parent), xaml),
            $"add {entry.Name}");

        Log($"  {entry.Name} added to {parent.Name}");
    }

    /// <summary>
    /// The element that should receive the drop, walking up from whatever was under the pointer.
    /// </summary>
    /// <remarks>
    /// Only a control that can hold children is a candidate: dropping onto a Button means dropping
    /// beside it, not inside it, because a Button's content is one thing and replacing it silently
    /// would delete what was there.
    /// </remarks>
    private static (XamlElement Parent, Control? Control) ParentFor(
        XamlObjectMap map, XamlElement root, Control? over)
    {
        for (Control? current = over; current is not null; current = current.Parent as Control)
        {
            if (current is not Panel and not ContentControl and not Decorator)
            {
                continue;
            }

            if (map.GetElement(current) is { } element && CanHoldChildren(current))
            {
                return (element, current);
            }
        }

        return (root, null);
    }

    private static bool CanHoldChildren(Control control) => control switch
    {
        Panel => true,
        Decorator decorator => decorator.Child is null,
        ContentControl content => content.Content is null,
        _ => false,
    };

    /// <summary>Adds a position to a snippet, for a parent that honours one.</summary>
    private static string WithPosition(ToolboxEntry entry, Point at)
    {
        string position = string.Create(
            CultureInfo.InvariantCulture,
            $" Canvas.Left=\"{Math.Round(at.X)}\" Canvas.Top=\"{Math.Round(at.Y)}\"");

        int close = entry.Xaml.LastIndexOf("/>", StringComparison.Ordinal);

        return close < 0
            ? entry.Xaml
            : entry.Xaml[..close] + position.TrimEnd() + " " + entry.Xaml[close..];
    }

    private static int IndexAtEnd(XamlElement parent) => parent.ContentElements.Count();
}
