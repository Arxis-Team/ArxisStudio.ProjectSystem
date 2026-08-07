using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;
using Avalonia.Media;

namespace FormsDesigner.ViewModels;

public sealed partial class DesignerViewModel
{
    /// <summary>What the selected element sets, and nothing it does not.</summary>
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    /// <summary>The same rows under the design's headings.</summary>
    public ObservableCollection<PropertyGroup> PropertyGroups { get; } = [];

    /// <summary>The selected element's type, for the inspector's header.</summary>
    public string SelectedType => Selected is null ? string.Empty : Selected.Name.LocalName;

    /// <summary>
    /// The namespace the selected control's type lives in, which the design shows beside the type.
    /// </summary>
    /// <remarks>
    /// Read off the live object rather than the document, because the document says a prefix and a
    /// local name and the answer to "which Button is this" is the resolved type. A document naming
    /// a type nothing resolved has no namespace to show, and shows none.
    /// </remarks>
    public string SelectedNamespace => LiveSelection()?.GetType().Namespace ?? string.Empty;

    /// <summary>The type and its namespace, the way the design writes them under the name.</summary>
    public string SelectedTypeLine => SelectedNamespace is { Length: > 0 } space
        ? $"{SelectedType} · {space}"
        : SelectedType;

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
        Raise(nameof(SelectedTypeLine));
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

        Control? live = LiveSelection();
        XamlLoadSession? session = ActiveForm?.Session;

        foreach (XamlAttribute attribute in element.Attributes)
        {
            // Namespace declarations are not properties of the control, they are how the file names
            // its vocabularies, and offering them for editing invites breaking every type in scope.
            if (attribute.Name.Prefix == "xmlns" || attribute.Name.LocalName == "xmlns")
            {
                continue;
            }

            string name = attribute.Name.ToString();

            Properties.Add(RowFor(element, attribute, name, live, session));
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

    /// <summary>The live control the selection stands for, when there is one.</summary>
    private Control? LiveSelection() =>
        Selected is { } element && ActiveForm?.Objects is { } map
            ? map.GetObject(element) as Control
            : null;

    /// <summary>
    /// Builds a row, asking the member what kind of value it holds.
    /// </summary>
    /// <remarks>
    /// The document alone cannot answer this — every attribute is text there — so the descriptor is
    /// what turns <c>IsEnabled="True"</c> into a checkbox and <c>Dock="Right"</c> into the four
    /// values <c>Dock</c> allows. A member nothing resolved still gets a row: a document may name a
    /// property of a type the project has not compiled yet, and refusing to show it would hide what
    /// the file says.
    /// </remarks>
    private PropertyRow RowFor(
        XamlElement element,
        XamlAttribute attribute,
        string name,
        Control? live,
        XamlLoadSession? session)
    {
        string text = attribute.GetValueText();

        // An expression is not a value, and a row that let one be typed over would replace a
        // binding with whatever the text happened to look like.
        if (attribute.GetValue() is XamlMarkupExtensionValue)
        {
            return Row(PropertyEditor.Expression, [], isReadOnly: true, static _ => null);
        }

        XamlMemberDescriptor? member = null;

        if (live is not null && session is not null && !attribute.IsDirective)
        {
            try
            {
                member = session.GetMember(live, name);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                member = null;
            }
        }

        if (member is not { IsResolved: true })
        {
            return Row(PropertyEditor.Text, [], attribute.IsDirective, static _ => null);
        }

        Type value = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;

        (PropertyEditor editor, IReadOnlyList<string> choices) = value switch
        {
            _ when value == typeof(bool) => (PropertyEditor.Flag, (IReadOnlyList<string>)[]),
            _ when value.IsEnum => Choices(value),
            _ when typeof(IBrush).IsAssignableFrom(value) || value == typeof(Color) =>
                (PropertyEditor.Colour, (IReadOnlyList<string>)[]),
            _ when value == typeof(double) || value == typeof(float)
                || value == typeof(int) || value == typeof(long) => (PropertyEditor.Number, (IReadOnlyList<string>)[]),
            _ => (PropertyEditor.Text, (IReadOnlyList<string>)[]),
        };

        XamlMemberDescriptor resolved = member;

        return Row(
            editor,
            choices,
            member.IsReadOnly || !member.CanWrite,
            typed => resolved.ConvertFromText(typed) is { Succeeded: false } failed
                ? failed.Error ?? $"not a {value.Name}"
                : null);

        static (PropertyEditor, IReadOnlyList<string>) Choices(Type value)
        {
            string[] names = Enum.GetNames(value);

            // Few enough to lay out side by side is the design's rule for a segmented control, and
            // four is where a row of them stops fitting the inspector's width.
            return (names.Length <= 4 ? PropertyEditor.Segmented : PropertyEditor.Choice, names);
        }

        PropertyRow Row(
            PropertyEditor editor,
            IReadOnlyList<string> choices,
            bool isReadOnly,
            Func<string, string?> validate)
        {
            var row = new PropertyRow(
                name,
                text,
                attribute.IsDirective,
                editor,
                choices,
                isReadOnly,
                validate,
                (_, written) => RunDetached(() => SetPropertyAsync(element, name, written)));

            row.Prime();

            return row;
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
