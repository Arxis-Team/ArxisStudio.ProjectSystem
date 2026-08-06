using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Avalonia.Media;

namespace FormsDesigner.ViewModels;

/// <summary>
/// The design's icon set, and the colour each kind of control is drawn in.
/// </summary>
/// <remarks>
/// <para>
/// The paths are the mockup's own, on its 16-unit grid. They are here rather than in the XAML
/// because both the hierarchy and the palette draw the same glyph for the same control, and a
/// duplicated path is a pair that drifts.
/// </para>
/// <para>
/// The colour is part of the icon, not decoration: the design gives layout panels one colour, text
/// entry another, lists a third, so a tree of thirty rows can be read by shape and hue before any of
/// the names are. A control nothing recognises gets the neutral one rather than a guess.
/// </para>
/// </remarks>
public static class Glyphs
{
    private const string Window = "M2.5 3.5h11v9h-11zM2.5 6h11";
    private const string Grid = "M3 3.5h10v9H3zM8 3.5v9M3 8h10";
    private const string Border = "M3.5 3.5h9v9h-9z";
    private const string TextBox = "M2 5h12v6H2zM4.5 7v2";
    private const string List = "M3 3.5h10v9H3zM5.5 6.5h5M5.5 9.5h5";
    private const string Stack = "M3 4h10M3 8h10M3 12h10";
    private const string Scroll = "M3 3.5h8.5v9H3zM13.5 5.5v5";
    private const string Dock = "M3 3.5h10v9H3zM3 6.5h10M7 6.5v6";
    private const string Button = "M2.5 5.5h11v5h-11z";
    private const string Tabs = "M3 6.5h10v6H3zM3 6.5V3.5h4.5v3";
    private const string Check = "M3 3h10v10H3zM5.5 8.2l1.8 1.8 3.4-4";
    private const string Combo = "M2 5h12v6H2zM9.5 7.2l1.5 1.5 1.5-1.5";
    private const string Slider = "M2 8h4M10 8h4M8 6a2 2 0 1 1 0 4 2 2 0 0 1 0-4";
    private const string Toggle = "M5.5 4.5h5a3.5 3.5 0 0 1 0 7h-5a3.5 3.5 0 0 1 0-7zM10.4 6.2a1.8 1.8 0 1 1-.01 0";
    private const string TextBlock = "M3.5 4h9M8 4v8.5";
    private const string Image = "M2.5 3.5h11v9h-11zM2.5 10l3.5-3 3 2.5 2-1.5 2.5 2";
    private const string DataGrid = "M2.5 3.5h11v9h-11zM2.5 6.5h11M2.5 9.5h11M8 6.5v6";
    private const string Progress = "M2 6.5h12v3H2zM4 8h4.5";
    private const string PathIcon = "M3 12.5C5 6 11 10 13 3.5";
    private const string Document = "M4 2.5h5.5L12.5 6v7.5H4zM9.5 2.5V6h3";
    private const string Box = "M3 3h10v10H3zM3 6.5h10";

    /// <summary>The glyph for a control, by its type name as the document spells it.</summary>
    private static readonly FrozenDictionary<string, string> Paths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Window"] = Window,
            ["UserControl"] = Window,
            ["Grid"] = Grid,
            ["Canvas"] = Grid,
            ["Border"] = Border,
            ["TextBox"] = TextBox,
            ["AutoCompleteBox"] = TextBox,
            ["NumericUpDown"] = TextBox,
            ["ListBox"] = List,
            ["ItemsControl"] = List,
            ["ListBoxItem"] = List,
            ["StackPanel"] = Stack,
            ["WrapPanel"] = Stack,
            ["ScrollViewer"] = Scroll,
            ["DockPanel"] = Dock,
            ["Button"] = Button,
            ["RepeatButton"] = Button,
            ["ToggleButton"] = Button,
            ["TabControl"] = Tabs,
            ["TabItem"] = Tabs,
            ["CheckBox"] = Check,
            ["RadioButton"] = Check,
            ["ComboBox"] = Combo,
            ["Slider"] = Slider,
            ["ToggleSwitch"] = Toggle,
            ["TextBlock"] = TextBlock,
            ["Label"] = TextBlock,
            ["Image"] = Image,
            ["DataGrid"] = DataGrid,
            ["ProgressBar"] = Progress,
            ["Separator"] = Progress,
            ["Path"] = PathIcon,
            ["Panel"] = Grid,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which of the design's five hues a control is drawn in.</summary>
    private static readonly FrozenDictionary<string, string> Hues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Window"] = "Acc",
            ["UserControl"] = "Acc",
            ["Button"] = "Acc",
            ["RepeatButton"] = "Acc",
            ["ToggleButton"] = "Acc",
            ["TabControl"] = "Acc",
            ["TabItem"] = "Acc",
            ["Slider"] = "Acc",
            ["ProgressBar"] = "Acc",
            ["Border"] = "Org",
            ["StackPanel"] = "Org",
            ["WrapPanel"] = "Org",
            ["DockPanel"] = "Org",
            ["Image"] = "Org",
            ["TextBox"] = "Pur",
            ["AutoCompleteBox"] = "Pur",
            ["NumericUpDown"] = "Pur",
            ["ComboBox"] = "Pur",
            ["DataGrid"] = "Pur",
            ["ListBox"] = "Grn",
            ["ItemsControl"] = "Grn",
            ["ListBoxItem"] = "Grn",
            ["ScrollViewer"] = "Grn",
            ["CheckBox"] = "Grn",
            ["RadioButton"] = "Grn",
            ["ToggleSwitch"] = "Grn",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The glyph for a control type, or a plain box for one nothing recognises.</summary>
    public static Geometry For(string? typeName) =>
        Geometry.Parse(typeName is not null && Paths.TryGetValue(Bare(typeName), out string? path) ? path : Box);

    /// <summary>The resource key of the colour a control type is drawn in.</summary>
    public static string HueOf(string? typeName) =>
        typeName is not null && Hues.TryGetValue(Bare(typeName), out string? hue) ? hue : "Fg2";

    /// <summary>The glyph for a document, for the tabs and the project list.</summary>
    public static Geometry DocumentGlyph { get; } = Geometry.Parse(Document);

    /// <summary>A name without its prefix, because <c>controls:Thing</c> is a Thing.</summary>
    private static string Bare(string name)
    {
        int colon = name.LastIndexOf(':');

        return colon < 0 ? name : name[(colon + 1)..];
    }
}
