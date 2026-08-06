using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ArxisStudio.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace FormsDesigner.ViewModels;

/// <summary>One row of the document's tree, flattened for a list rather than nested.</summary>
/// <remarks>
/// Flat, with the depth carried as an indent, because that is what the design draws and because a
/// nested tree control would own expansion state the document does not have. Every row is an
/// element of the document, so selecting one is selecting markup.
/// </remarks>
public sealed class HierarchyRow(XamlElement element, int depth, bool hasChildren)
{
    public XamlElement Element { get; } = element;

    public double Indent { get; } = 8 + (depth * 14);

    public string Name { get; } =
        element.GetDirective("Name") is { Length: > 0 } named ? named : element.Name.LocalName;

    /// <summary>The type, and what it holds, which is the second line of every row in the design.</summary>
    public string TypeLabel { get; } = Describe(element);

    public Geometry Glyph { get; } = Glyphs.For(element.Name.LocalName);

    public string Hue { get; } = Glyphs.HueOf(element.Name.LocalName);

    public double ChevronOpacity { get; } = hasChildren ? 1 : 0;

    private static string Describe(XamlElement element)
    {
        int children = element.ContentElements.Count();

        return children switch
        {
            0 => element.Name.LocalName,
            1 => element.Name.LocalName + " · 1 child",
            _ => $"{element.Name.LocalName} · {children} children",
        };
    }
}

/// <summary>What the editor shows in the middle: the surface, the markup, or both.</summary>
public enum DocumentView
{
    Design,
    Xaml,
    Split,
}

public sealed partial class DesignerViewModel
{
    /// <summary>The open document's tree, in document order.</summary>
    public ObservableCollection<HierarchyRow> Hierarchy { get; } = [];

    /// <summary>The path from the root to what is selected, which the design puts above the canvas.</summary>
    public ObservableCollection<string> Breadcrumb { get; } = [];

    public HierarchyRow? SelectedHierarchyRow
    {
        get;
        set
        {
            if (Set(ref field, value) && value is not null && !_syncingHierarchy)
            {
                Selected = value.Element;
            }
        }
    }

    /// <summary>Design, XAML or both, which is the segmented control in the breadcrumb bar.</summary>
    public DocumentView View
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(IsDesignVisible));
                Raise(nameof(IsXamlVisible));
                Raise(nameof(IsDesignView));
                Raise(nameof(IsXamlView));
                Raise(nameof(IsSplitView));
            }
        }
    } = DocumentView.Design;

    public bool IsDesignVisible => View is DocumentView.Design or DocumentView.Split;

    public bool IsXamlVisible => View is DocumentView.Xaml or DocumentView.Split;

    public bool IsDesignView => View == DocumentView.Design;

    public bool IsXamlView => View == DocumentView.Xaml;

    public bool IsSplitView => View == DocumentView.Split;

    public RelayCommand ShowDesignCommand { get; private set; } = null!;

    public RelayCommand ShowXamlCommand { get; private set; } = null!;

    public RelayCommand ShowSplitCommand { get; private set; } = null!;

    public RelayCommand ZoomInCommand { get; private set; } = null!;

    public RelayCommand ZoomOutCommand { get; private set; } = null!;

    public RelayCommand ZoomResetCommand { get; private set; } = null!;

    public RelayCommand ToggleThemeCommand { get; private set; } = null!;

    /// <summary>The document's text, for the XAML view.</summary>
    public string DocumentText
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    public bool IsDark
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(IsLight));
                Raise(nameof(ThemeName));
            }
        }
    } = true;

    public bool IsLight => !IsDark;

    /// <summary>What the status bar says on the left.</summary>
    public string StatusLeft => IsLoaded
        ? $"{Status} · {Describe(ProjectForms.Count, "form")}"
        : "No project";

    /// <summary>The size of the form being designed, as the design shows it above the canvas.</summary>
    public string CanvasCaption => ActiveForm is { } form
        ? $"⌖ {form.Name}   {form.Width:F0} × {form.Height:F0} · {Zoom * 100:F0}%"
        : string.Empty;

    private bool _syncingHierarchy;

    private void InitialiseShell()
    {
        ShowDesignCommand = new RelayCommand(() => View = DocumentView.Design);
        ShowXamlCommand = new RelayCommand(() => View = DocumentView.Xaml);
        ShowSplitCommand = new RelayCommand(() => View = DocumentView.Split);

        ZoomInCommand = new RelayCommand(() => Zoom = Math.Min(4, Zoom * 1.1));
        ZoomOutCommand = new RelayCommand(() => Zoom = Math.Max(0.1, Zoom / 1.1));
        ZoomResetCommand = new RelayCommand(() => Zoom = 1);

        ToggleThemeCommand = new RelayCommand(SwitchTheme);
    }

    /// <summary>
    /// Switches the whole application between the design's two variants.
    /// </summary>
    /// <remarks>
    /// On the application rather than on the window, because the palette is a resource dictionary
    /// keyed by variant and every panel reads it — including the canvas grid, which belongs to a
    /// library and would otherwise stay dark on a light desk.
    /// </remarks>
    private void SwitchTheme()
    {
        IsDark = !IsDark;

        if (Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    /// <summary>
    /// Rebuilds the tree and the document text from whatever form is active.
    /// </summary>
    /// <remarks>
    /// From the document, not from the live objects. The tree a designer navigates is the file's
    /// structure — a template's parts are not in it, and neither is anything a control invented for
    /// itself — which is also why every row can be selected and edited.
    /// </remarks>
    private void RebuildHierarchy()
    {
        Hierarchy.Clear();

        DocumentText = ActiveForm?.Document?.SourceText.ToString() ?? string.Empty;

        if (ActiveForm?.Document?.Root is not { } root)
        {
            ShowBreadcrumb();

            return;
        }

        Walk(root, 0);

        SyncHierarchySelection();
        ShowBreadcrumb();

        void Walk(XamlElement element, int depth)
        {
            List<XamlElement> children = [.. element.ContentElements];

            Hierarchy.Add(new HierarchyRow(element, depth, children.Count > 0));

            foreach (XamlElement child in children)
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Moves the tree's highlight to whatever is selected, without answering back.</summary>
    private void SyncHierarchySelection()
    {
        _syncingHierarchy = true;

        try
        {
            SelectedHierarchyRow = Selected is null
                ? null
                : Hierarchy.FirstOrDefault(row => ReferenceEquals(row.Element, Selected));
        }
        finally
        {
            _syncingHierarchy = false;
        }
    }

    private void ShowBreadcrumb()
    {
        Breadcrumb.Clear();

        if (Selected is null)
        {
            return;
        }

        var trail = new Stack<string>();

        for (XamlElement? current = Selected; current is not null; current = current.Parent as XamlElement)
        {
            trail.Push(current.GetDirective("Name") is { Length: > 0 } named
                ? named
                : current.Name.LocalName);
        }

        foreach (string step in trail)
        {
            Breadcrumb.Add(step);
        }
    }
}
