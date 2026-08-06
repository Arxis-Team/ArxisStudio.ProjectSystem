using System;
using System.Collections.ObjectModel;
using System.Linq;
using ArxisStudio.Markup.Xaml;
using Avalonia.Media;

namespace FormsDesigner.ViewModels;

/// <summary>
/// One editable property of the selected element.
/// </summary>
/// <remarks>
/// A row is a fact about the <em>document</em>: the attribute's name as written, and its text. It is
/// deliberately not a fact about the live control, which would report the value every property has
/// rather than the ones this file sets — a Button has upwards of a hundred, three of which the
/// author wrote, and an inspector listing all of them tells nobody which ones matter.
/// </remarks>
public sealed class PropertyRow : Observable
{
    private readonly Action<PropertyRow, string> _commit;

    internal PropertyRow(string name, string value, bool isDirective, Action<PropertyRow, string> commit)
    {
        Name = name;
        IsDirective = isDirective;
        _commit = commit;
        _value = value;
    }

    public string Name { get; }

    /// <summary>Whether this is <c>x:Name</c> and its kind rather than an ordinary property.</summary>
    public bool IsDirective { get; }

    private string _value;

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                _commit(this, value);
            }
        }
    }
}

/// <summary>One of the inspector's headings, and the rows under it.</summary>
public sealed record PropertyGroup(string Name, System.Collections.Generic.IReadOnlyList<PropertyRow> Rows);

public sealed partial class DesignerViewModel
{
    /// <summary>What the selected element sets, and nothing it does not.</summary>
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    /// <summary>The same rows under the design's headings.</summary>
    public ObservableCollection<PropertyGroup> PropertyGroups { get; } = [];

    /// <summary>The selected element's type, for the inspector's header.</summary>
    public string SelectedType => Selected is null ? string.Empty : Selected.Name.LocalName;

    public string SelectedQualifier => Selected is null
        ? "nothing selected"
        : Selected.Name.Prefix is { Length: > 0 } prefix
            ? $"{Selected.Name.LocalName} · {prefix}"
            : $"{Selected.Name.LocalName} · Avalonia.Controls";

    public Geometry SelectedGlyph => Glyphs.For(Selected?.Name.LocalName);

    /// <summary>
    /// The chips the design puts under the header: what the element declares beyond its properties.
    /// </summary>
    /// <remarks>
    /// Directives and classes, because those are the facts about an element that are not values —
    /// <c>x:Name</c> says it can be found from code, a class says a style may reach it. The design
    /// draws them as chips precisely because they are not editable in the rows below.
    /// </remarks>
    public ObservableCollection<string> SelectedChips { get; } = [];

    /// <summary>
    /// Which of the design's three sections a property belongs to.
    /// </summary>
    /// <remarks>
    /// A lookup rather than reflection over the type. The inspector lists what the document sets, and
    /// what the document sets is a name — so the grouping is a fact about the name, and a property
    /// this table has never heard of goes to Content rather than being hidden.
    /// </remarks>
    private static string GroupOf(string name) => name switch
    {
        "Width" or "Height" or "MinWidth" or "MinHeight" or "MaxWidth" or "MaxHeight"
            or "Margin" or "Padding" or "HorizontalAlignment" or "VerticalAlignment"
            or "HorizontalContentAlignment" or "VerticalContentAlignment"
            or "Dock" or "Spacing" or "Orientation" or "ZIndex" => "Layout",

        "Background" or "Foreground" or "BorderBrush" or "BorderThickness" or "CornerRadius"
            or "FontSize" or "FontWeight" or "FontFamily" or "FontStyle" or "Opacity"
            or "BoxShadow" or "Classes" => "Appearance",

        _ when name.StartsWith("Canvas.", StringComparison.Ordinal)
            || name.StartsWith("Grid.", StringComparison.Ordinal)
            || name.StartsWith("DockPanel.", StringComparison.Ordinal) => "Layout",

        _ => "Content & Interaction",
    };

    /// <summary>The property to add, typed by hand, because a full member list needs reflection.</summary>
    /// <remarks>
    /// A designer with the project's assemblies loaded could offer every settable property of the
    /// selected type — the assembly context is right there. It is not done here because the useful
    /// version of that is a typed editor per property kind, which is a feature rather than a
    /// demonstration; a name and a value is enough to show that the edit reaches the file.
    /// </remarks>
    public string NewPropertyName
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    public string NewPropertyValue
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;

    public RelayCommand AddPropertyCommand { get; private set; } = null!;

    public RelayCommand DeleteSelectedCommand { get; private set; } = null!;

    private void InitialiseInspector()
    {
        AddPropertyCommand = new RelayCommand(
            () => Run(AddPropertyAsync),
            () => Selected is not null && NewPropertyName.Length > 0);

        DeleteSelectedCommand = new RelayCommand(
            () => Run(async () =>
            {
                if (ActiveForm is { } form && Selected is { } element)
                {
                    await DeleteAsync(form, element);
                }
            }),
            () => Selected is not null && ActiveForm is not null);
    }

    private void BuildInspector()
    {
        Properties.Clear();
        PropertyGroups.Clear();

        Raise(nameof(SelectedType));
        Raise(nameof(SelectedQualifier));
        Raise(nameof(SelectedGlyph));

        SelectedChips.Clear();

        if (Selected is not { } element)
        {
            return;
        }

        foreach (XamlAttribute directive in element.Directives)
        {
            SelectedChips.Add(directive.Name.ToString());
        }

        if (element.Attributes.FirstOrDefault(
            a => a.Name.IsUnprefixed("Classes")) is { } classes)
        {
            foreach (string name in classes.GetValueText().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                SelectedChips.Add("." + name);
            }
        }

        foreach (XamlAttribute attribute in element.Attributes)
        {
            // Namespace declarations are not properties of the control, they are how the file names
            // its vocabularies, and offering them for editing invites breaking every type in scope.
            if (attribute.Name.Prefix == "xmlns" || attribute.Name.LocalName == "xmlns")
            {
                continue;
            }

            string name = attribute.Name.ToString();

            Properties.Add(new PropertyRow(
                name,
                attribute.GetValueText(),
                attribute.IsDirective,
                (_, value) => RunDetached(() => SetPropertyAsync(element, name, value))));
        }

        // Grouped in the design's order, and a section with nothing in it is not drawn: an inspector
        // showing three empty headings tells somebody the control has no properties, which is the
        // opposite of what it means.
        foreach (string heading in new[] { "Layout", "Appearance", "Content & Interaction" })
        {
            PropertyRow[] rows = [.. Properties.Where(row => GroupOf(row.Name) == heading)];

            if (rows.Length > 0)
            {
                PropertyGroups.Add(new PropertyGroup(heading, rows));
            }
        }
    }

    private async System.Threading.Tasks.Task SetPropertyAsync(XamlElement element, string name, string value)
    {
        if (ActiveForm is not { } form)
        {
            return;
        }

        await ApplyAsync(
            form,
            editor => editor.SetAttribute(element, XamlQualifiedName.Parse(name), value),
            $"set {name}");
    }

    private async System.Threading.Tasks.Task AddPropertyAsync()
    {
        if (ActiveForm is not { } form || Selected is not { } element)
        {
            return;
        }

        string name = NewPropertyName.Trim();
        string value = NewPropertyValue;

        await ApplyAsync(
            form,
            editor => editor.SetAttribute(element, XamlQualifiedName.Parse(name), value),
            $"add {name}");

        NewPropertyName = string.Empty;
        NewPropertyValue = string.Empty;

        // The element object is replaced by the edit, so the inspector is rebuilt from whatever the
        // new document has in its place rather than from the stale one still being pointed at.
        Reselect(form, element);
    }

    /// <summary>
    /// Finds the selected element again in a document that has just been rebuilt.
    /// </summary>
    /// <remarks>
    /// Every edit produces a new document with new element objects, so the selection has to be
    /// re-established rather than kept. Position in the tree is what identifies it — the same walk
    /// the old element took from the root — because a name is optional and a reference is stale.
    /// </remarks>
    private void Reselect(FormViewModel form, XamlElement previous)
    {
        if (form.Document is not { } document)
        {
            return;
        }

        int[] path = [.. PathTo(previous)];

        XamlElement? found = document.Root;

        foreach (int step in path)
        {
            found = found?.ContentElements.ElementAtOrDefault(step);
        }

        Selected = found;
    }

    private static System.Collections.Generic.Stack<int> PathTo(XamlElement element)
    {
        var steps = new System.Collections.Generic.Stack<int>();

        for (XamlElement? current = element; current?.Parent is XamlElement parent; current = parent)
        {
            steps.Push(parent.ContentElements.ToList().IndexOf(current));
        }

        return steps;
    }
}
