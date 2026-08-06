using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;

namespace FormsDesigner.ViewModels;

public sealed partial class DesignerViewModel
{
    /// <summary>The form the panels are about.</summary>
    public FormViewModel? ActiveForm
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Selected = null;
                RefreshAllCommands();
            }
        }
    }

    /// <summary>The control the inspector is about, as an element of the document.</summary>
    /// <remarks>
    /// An element rather than a live object, because the document is what gets edited. The live
    /// object is how the user pointed at it and is of no further interest once the pointing is done.
    /// </remarks>
    public XamlElement? Selected
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(SelectedName));
                BuildInspector();
                RefreshAllCommands();
            }
        }
    }

    public string SelectedName => Selected is null
        ? "nothing selected"
        : Selected.Name.ToString() + (Selected.GetDirective("Name") is { Length: > 0 } name
            ? $"  “{name}”"
            : string.Empty);

    /// <summary>
    /// Follows a click on the canvas back to the element that produced the control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object map runs both ways, and this is the direction that makes a designer feel like one:
    /// a person points at a button on screen and the tool knows which line of the file drew it.
    /// Templates and styles produce controls no element made — a button's own border, the text
    /// inside it — so the walk goes up through the parents until it finds one that is mapped, which
    /// is how clicking a button's label selects the button.
    /// </para>
    /// <para>
    /// The walk ends at the document's root rather than at nothing. Not every control on screen has
    /// a mapped ancestor — a part a template produced inside another part need not — and selecting
    /// the form is a better answer than selecting nothing, because the form is a thing worth
    /// selecting: its title and its size are edited there.
    /// </para>
    /// </remarks>
    public void SelectFromCanvas(FormViewModel form, Control? control)
    {
        ActiveForm = form;

        if (form.Objects is not { } map)
        {
            Selected = null;

            return;
        }

        for (Control? current = control; current is not null; current = current.Parent as Control)
        {
            if (map.GetElement(current) is { } element)
            {
                Selected = element;

                return;
            }
        }

        Selected = form.Document?.Root;
    }

    /// <summary>Finds the live control an element produced, for the editor to select.</summary>
    public static Control? ControlFor(FormViewModel form, XamlElement element) =>
        form.Objects?.GetObject(element) as Control;

    /// <summary>
    /// Applies an edit to the document and to the live objects, in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One route for every change, which is what keeps the canvas incapable of disagreeing with the
    /// file. The editor builds the new document, the session works out what that means for the
    /// objects already on screen, and only what actually changed is rebuilt — a property set on one
    /// button does not tear down the form around it.
    /// </para>
    /// <para>
    /// The document is adopted whatever the outcome, because the edit is real either way: a change
    /// the live tree could not follow is still a change to the file, and pretending otherwise would
    /// silently drop the user's work.
    /// </para>
    /// </remarks>
    private async Task ApplyAsync(FormViewModel form, Action<XamlDocumentEditor> edit, string what)
    {
        if (form.Document is not { } document || form.Session is not { } session)
        {
            return;
        }

        XamlDocumentEditor editor = document.Edit();

        edit(editor);

        if (!editor.HasChanges)
        {
            return;
        }

        XamlDocument updated = editor.Apply();

        XamlUpdateResult result = await session.ApplyDocumentUpdateAsync(updated, _shutdown.Token);

        form.Adopt(session.Document);
        form.IsDirty = true;

        // The root object can be replaced outright when a change reaches far enough, and the canvas
        // is holding the old one until it is told.
        RefreshRoot(form, session);

        if (!result.Applied)
        {
            Log($"  {what}: the live tree needed {result.Outcome}");
        }

        RefreshAllCommands();
    }

    private static void RefreshRoot(FormViewModel form, XamlLoadSession session)
    {
        if (!ReferenceEquals(form.Root, session.RootObject))
        {
            form.AdoptRoot(session);
        }
    }

    /// <summary>
    /// Writes a control's geometry into the document after the editor has moved or resized it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which attributes to write is a question about the parent, not about the control: inside a
    /// <c>Canvas</c> a position is <c>Canvas.Left</c> and <c>Canvas.Top</c>, and inside a
    /// <c>StackPanel</c> there is no position to write at all — the panel decides, and writing a
    /// margin to fake one would produce a file whose meaning changes the moment somebody adds a
    /// sibling.
    /// </para>
    /// <para>
    /// So a move in a flow layout is refused rather than approximated. The editor already declines
    /// to start such a gesture when nothing answers its reorder request, and this is the other half
    /// of the same honesty.
    /// </para>
    /// </remarks>
    private async Task WriteGeometryAsync(
        FormViewModel form, XamlElement element, Control control, bool moved, bool resized)
    {
        Control? parent = control.Parent as Control;

        bool positioned = parent is Canvas or null;

        await ApplyAsync(
            form,
            editor =>
            {
                if (moved && positioned && parent is Canvas)
                {
                    Set(editor, element, "Canvas.Left", Canvas.GetLeft(control));
                    Set(editor, element, "Canvas.Top", Canvas.GetTop(control));
                }

                if (resized)
                {
                    Set(editor, element, "Width", control.Bounds.Width);
                    Set(editor, element, "Height", control.Bounds.Height);
                }
            },
            "geometry");

        if (moved && !positioned)
        {
            Log($"  {element.Name} sits in a {parent?.GetType().Name}, which owns its position — "
                + "nothing written");
        }

        static void Set(XamlDocumentEditor editor, XamlElement element, string name, double value)
        {
            if (double.IsFinite(value))
            {
                editor.SetAttribute(
                    element,
                    XamlQualifiedName.Parse(name),
                    Math.Round(value).ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Writes a finished gesture into the document. Called by the view.</summary>
    public void WriteGeometry(FormViewModel form, Control control, bool moved, bool resized)
    {
        if (form.Objects?.GetElement(control) is { } element)
        {
            RunDetached(() => WriteGeometryAsync(form, element, control, moved, resized));
        }
    }

    /// <summary>Answers the editor's delete request. Called by the view.</summary>
    public void DeleteFromCanvas(FormViewModel form, Control control)
    {
        if (form.Objects?.GetElement(control) is { } element)
        {
            RunDetached(() => DeleteAsync(form, element));
        }
    }

    /// <summary>Removes the selected element, which is a structural edit and therefore Markup's.</summary>
    private async Task DeleteAsync(FormViewModel form, XamlElement element)
    {
        await ApplyAsync(form, editor => editor.RemoveElement(element), "delete");

        Selected = null;

        Log($"  removed {element.Name}");
    }

    /// <summary>Writes the document back to its file.</summary>
    /// <remarks>
    /// The whole text, from the document, rather than the text changes — the changes are how the
    /// editor got here and the document is what it means. A form saved this way keeps every scrap of
    /// formatting the user wrote, because the document was only ever edited where it was edited.
    /// </remarks>
    private async Task SaveAsync()
    {
        if (ActiveForm is not { Document: { } document } form)
        {
            return;
        }

        await System.IO.File.WriteAllTextAsync(form.File.Value, document.SourceText.ToString(), _shutdown.Token);

        form.IsDirty = false;

        Log($"Saved {form.Name}");

        RefreshAllCommands();
    }
}
