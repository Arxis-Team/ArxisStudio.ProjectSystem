using System;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Attached;
using ArxisStudio.Markup.Xaml.Design;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;

namespace FormsDesigner.ViewModels;

/// <summary>
/// One form open on the canvas: the file, the document, and the live objects built from it.
/// </summary>
/// <remarks>
/// <para>
/// The three are kept together because they are three views of one thing and they must never drift.
/// <see cref="Document"/> is the truth — every edit goes through it — <see cref="Root"/> is what the
/// canvas shows, and <see cref="Objects"/> is the map between them, which is what turns a click on a
/// button into the element in the file that produced it.
/// </para>
/// <para>
/// <see cref="Location"/>, <see cref="Width"/> and <see cref="Height"/> are the form's place on the
/// designer's infinite surface and have nothing to do with the form's own layout. They belong here
/// because the editor binds its container to them, and they are deliberately not written to the
/// document: where a designer parked a window is not a fact about the window.
/// </para>
/// </remarks>
public sealed class FormViewModel : Observable, IAsyncDisposable
{
    public FormViewModel(CanonicalPath file, Point location)
    {
        File = file;
        Name = file.FileName;
        Location = location;

        FollowTheSurface();
    }

    /// <summary>The file this form was read from and will be written back to.</summary>
    public CanonicalPath File { get; }

    public string Name
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>Where the form sits on the designer's surface.</summary>
    public Point Location
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(ChromeLeft));
                Raise(nameof(ChromeTop));
            }
        }
    }

    public double Width
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(SizeText));
                FollowTheCard();
            }
        }
    } = 480;

    public double Height
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(SizeText));
                FollowTheCard();
            }
        }
    } = 360;

    /// <summary>
    /// Makes the form the size the card is, as the card is dragged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The card follows the pointer, and the form inside it used to follow the document — which does
    /// not change until the drag ends. So a resize showed the form at its old size inside a card at
    /// its new one: growing left a form too small for its card, and shrinking left one hanging out
    /// past every edge, until the button came up and it snapped into place. The size in the caption
    /// was the card's and the size in the inspector was the document's, and for the length of the
    /// gesture they disagreed.
    /// </para>
    /// <para>
    /// This is the one thing the designer writes to the live tree, and it writes nothing else and
    /// nowhere else: a preview lasts as long as the gesture, and the document edit on completion is
    /// what makes it true. The document remains the only thing an edit touches — a preview that was
    /// never committed is corrected by the next load, which reads the file.
    /// </para>
    /// </remarks>
    private void FollowTheCard()
    {
        if (Root is not { } root)
        {
            return;
        }

        if (double.IsFinite(Width) && Width > 0)
        {
            root.Width = Width;
        }

        if (double.IsFinite(Height) && Height > 0)
        {
            root.Height = Height;
        }
    }

    /// <summary>The document, which is the only thing any edit touches.</summary>
    public XamlDocument? Document
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>The live control tree the document produced.</summary>
    /// <remarks>
    /// What the document means, not what the canvas shows — see <see cref="Surface"/> for the
    /// difference and why there is one.
    /// </remarks>
    public Control? Root
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>
    /// The control the canvas actually hosts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same object as <see cref="Root"/> would be, except that a window cannot be a child of
    /// anything: Avalonia gives one a <c>TopLevelHost</c> parent the moment it is constructed, so
    /// putting it in a <c>ContentControl</c> throws during layout, off the stack of whatever asked
    /// for the form. A window-rooted form used to leave the canvas empty with nothing said about it.
    /// </para>
    /// <para>
    /// This sample used to answer that itself — take the content out, host that, carry the data
    /// context across by hand. It is <c>ArxisStudio.Markup.Xaml.Design</c>'s answer now, which is
    /// where it belongs: every host that shows forms meets the same wall, and would meet the same
    /// four consequences after it. What is left here is the frame the designer draws around it.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Hit-testing is off because a loaded form must not live its own life: a button would eat the
    /// press and a text box would take focus and accept typing. The editor does not need it — it
    /// works out what was pointed at from rectangles in world coordinates — and in
    /// <c>ContentMode="Loaded"</c> its own theme switches this off for the same reason. Annotated
    /// mode leaves it to the host, so the host does it.
    /// </remarks>
    public XamlDesignSurface Surface { get; } = new() { IsHitTestVisible = false };

    /// <summary>Follows what the stand-in reports about the form, which it keeps current.</summary>
    /// <remarks>
    /// Observed rather than copied on publication. A publication happens once per session, and the
    /// title is a property an inspector edits — so a snapshot went stale the first time anybody used
    /// the feature the title bar exists to show, while the doc above claimed the opposite.
    /// </remarks>
    private void FollowTheSurface() =>
        Surface.GetObservable(XamlDesignSurface.TitleProperty).Subscribe(new AnonymousObserver<string?>(_ =>
        {
            Raise(nameof(WindowTitle));
            Raise(nameof(ChromeTop));
        }));

    /// <summary>Where the designer's chrome starts, which is the card's left edge.</summary>
    public double ChromeLeft => Location.X;

    /// <summary>
    /// Where the designer's chrome starts vertically, which is far enough above the card for it.
    /// </summary>
    /// <remarks>
    /// The chrome hangs above the form rather than over it: a caption, and for a window the title
    /// bar that belongs to the window rather than to its content. Both are laid out downwards from
    /// here, so this is the card's top edge less however much of them there is.
    /// </remarks>
    public double ChromeTop => Location.Y - (WindowTitle is { Length: > 0 } ? 60 : 22);

    /// <summary>The title to draw, when the root is a window, and nothing otherwise.</summary>
    /// <remarks>
    /// The title is a property of a window nobody can see, so drawing it is the only way it appears.
    /// It is read off the surface rather than off the window, because the surface keeps it current:
    /// an edit to <c>Title</c> in the inspector changes what is drawn without anything being rebuilt.
    /// </remarks>
    public string? WindowTitle => Surface.IsTopLevel
        ? Surface.Title is { Length: > 0 } title ? title : Name
        : null;

    /// <summary>The map between the two, in both directions.</summary>
    public XamlObjectMap? Objects => Session?.Objects;

    /// <summary>
    /// What the document was before each edit, and what it was before each undo.
    /// </summary>
    /// <remarks>
    /// Documents rather than a list of what changed, because a document already is one: every edit
    /// produces a whole new immutable one, so remembering the previous is remembering everything,
    /// and going back is applying it the same way any other change is applied. An editor that
    /// recorded inverse operations would have to be right about each of them; this cannot be wrong
    /// about what the file said, because it is what the file said.
    /// </remarks>
    private readonly System.Collections.Generic.Stack<XamlDocument> _undo = new();

    private readonly System.Collections.Generic.Stack<XamlDocument> _redo = new();

    /// <summary>The document as the file has it, for telling an edited form from a returned one.</summary>
    private XamlDocument? _saved;

    internal bool CanUndo => _undo.Count > 0;

    internal bool CanRedo => _redo.Count > 0;

    /// <summary>Records where an edit started from. A new edit is the end of any redo path.</summary>
    internal void Remember(XamlDocument before)
    {
        _undo.Push(before);
        _redo.Clear();
    }

    /// <summary>The document to go back to, if there is one.</summary>
    internal XamlDocument? StepBack(XamlDocument current)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        _redo.Push(current);

        return _undo.Pop();
    }

    /// <summary>And the one to come forward to.</summary>
    internal XamlDocument? StepForward(XamlDocument current)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        _undo.Push(current);

        return _redo.Pop();
    }

    /// <summary>A form freshly loaded or freshly saved has nothing behind it and nothing pending.</summary>
    internal void MarkSaved()
    {
        _saved = Document;

        IsDirty = false;
    }

    /// <summary>Whether the document differs from what the file holds, which an undo can undo.</summary>
    internal void Restated() => IsDirty = !ReferenceEquals(Document, _saved);

    /// <summary>Whether the document has edits the file does not have yet.</summary>
    public bool IsDirty
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Title));
            }
        }
    }

    /// <summary>Whether this is the form the canvas is showing.</summary>
    /// <remarks>
    /// Held here rather than worked out in the tab strip, because a tab has to answer it about itself
    /// and a template comparing its own item to a property of the window's data context needs a
    /// converter and a second binding to do what a boolean does.
    /// </remarks>
    public bool IsActive
    {
        get;
        internal set => Set(ref field, value);
    }

    /// <summary>What the tab and the canvas label show.</summary>
    public string Title => IsDirty ? Name + " •" : Name;

    /// <summary>The target mark the design floats above the form's card.</summary>
    public string Caption => "⌖ " + Name;

    /// <summary>And its size beside it, in the design's dim colour.</summary>
    public string SizeText => $"{Width:F0} × {Height:F0}";

    /// <summary>Why the form could not be shown, when it could not.</summary>
    public string? Problem
    {
        get;
        private set => Set(ref field, value);
    }

    internal XamlLoadSession? Session { get; private set; }

    /// <summary>
    /// Puts a freshly loaded session in place of whatever was there.
    /// </summary>
    /// <remarks>
    /// The old session is disposed after the new root is published rather than before, so the canvas
    /// never has a moment with nothing in it — and because disposing a session tears down objects the
    /// visual tree may still be walking.
    /// </remarks>
    internal async ValueTask AdoptAsync(XamlLoadSession session)
    {
        XamlLoadSession? previous = Session;

        Session = session;
        Document = session.Document;

        Publish(session);

        Problem = Root is null
            ? $"The document's root is {session.RootObject.GetType().Name}, which is not a Control."
            : null;

        // A form that has just been loaded is the file, and has no history behind it.
        _undo.Clear();
        _redo.Clear();

        MarkSaved();

        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    /// <summary>
    /// Republishes the root after an update replaced it.
    /// </summary>
    /// <remarks>
    /// An update that reaches far enough rebuilds the root object rather than patching it, and the
    /// canvas is holding the previous one until it is told otherwise.
    /// </remarks>
    internal void AdoptRoot(XamlLoadSession session) => Publish(session);

    /// <summary>
    /// Works out what to show from what the document produced.
    /// </summary>
    /// <remarks>
    /// Run again after every update, because an update that reaches far enough rebuilds the root —
    /// and because a rebuilt window arrives with its content back inside it.
    /// </remarks>
    private void Publish(XamlLoadSession session)
    {
        Root = session.RootObject as Control;

        Surface.Attach(session);

        MarkEditable(session);

        Raise(nameof(Objects));
    }

    /// <summary>
    /// Tells the editor which controls are the document's own, and therefore editable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the object map rather than worked out from the tree, because the map is the one
    /// thing that knows. A walk would have to guess about the stand-in that hosts the form, about
    /// anything a template generated, and about whatever the designer adds next — and it would guess
    /// silently.
    /// </para>
    /// <para>
    /// But the map holds more than the document's own objects, and marking all of them offered a
    /// button's own label as something to select and resize — a rectangle inside the button, for
    /// which the inspector and the tree both answered "Button", because that is the nearest thing
    /// with an element behind it. After an edit, when the button's template has been realised, the
    /// map picks the label up too.
    /// </para>
    /// <para>
    /// The test is the round trip rather than the origin, and it is the stricter of the two. Asking
    /// for the element behind a control <em>and</em> checking that the element leads back to the
    /// same control admits only what the document declared: a label paired to nothing fails the
    /// first half, and anything paired to its owner's element fails the second. <c>GetOrigin</c>
    /// would do for the label — since the map stopped claiming the document's origin for objects it
    /// found no declaration for — but it answers a question about provenance, and this one is about
    /// editability, which is what an element is.
    /// </para>
    /// <para>
    /// The root is the one document object deliberately left unmarked, because the card already
    /// stands for it. Marking it too gave the form two frames that both said <c>UserControl</c> and
    /// both edited the same element, and the inner one was the worse of the two: it is a control
    /// inside a container, so its handles could not take the form past the card it sits in — they
    /// re-measured it against the card instead. The card is the frame whose size <em>is</em> the
    /// form's, and resizing it writes the root's <c>Width</c> and <c>Height</c>.
    /// </para>
    /// <para>
    /// Everything else keeps working through the map rather than through this flag: a drop still
    /// finds the root as a container, the inspector still edits the root's own properties, and
    /// selecting the root row still lands on the card — <c>SelectDesignTarget</c> refuses an unmarked
    /// control and <c>ShowOnCanvas</c> falls through to the card, which is the answer it already gave
    /// for a window-rooted form.
    /// </para>
    /// </remarks>
    internal static void MarkEditable(XamlLoadSession session)
    {
        XamlObjectMap map = session.Objects;

        foreach (object produced in map.Objects)
        {
            if (ReferenceEquals(produced, session.RootObject))
            {
                continue;
            }

            if (produced is Control control
                && map.GetElement(control) is { } element
                && ReferenceEquals(map.GetObject(element), control))
            {
                Layout.SetIsTracked(control, true);
            }
        }
    }

    internal void Fail(string problem)
    {
        Problem = problem;
        Root = null;
    }

    /// <summary>Replaces the document without rebuilding the live tree.</summary>
    /// <remarks>
    /// Used after an edit the session applied itself: the session already holds the new document and
    /// the live objects to match, so re-reading either would be undoing work that is already correct.
    /// </remarks>
    internal void Adopt(XamlDocument document)
    {
        Document = document;
        Raise(nameof(Objects));
    }

    public async ValueTask DisposeAsync()
    {
        if (Session is { } session)
        {
            Session = null;

            await session.DisposeAsync();
        }

        Surface.Dispose();

        Root = null;
    }
}
