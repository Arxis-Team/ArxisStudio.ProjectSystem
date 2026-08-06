using System;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using Avalonia;
using Avalonia.Controls;

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
        set => Set(ref field, value);
    }

    public double Width
    {
        get;
        set => Set(ref field, value);
    } = 480;

    public double Height
    {
        get;
        set => Set(ref field, value);
    } = 360;

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
    /// The same object as <see cref="Root"/> for anything that can be a child, and the window's
    /// contents when the root is a <see cref="Window"/>. <b>A window cannot be a child of
    /// anything.</b> Avalonia gives one a <c>TopLevelHost</c> parent the moment it is constructed,
    /// so putting it in a <c>ContentControl</c> throws — and it throws during layout, off the stack
    /// of whatever asked for the form, which is why a window-rooted form left the canvas empty with
    /// nothing said about it.
    /// </para>
    /// <para>
    /// So the window's content is taken out of it and shown, with the designer drawing the frame.
    /// That is what every form designer does, and it is why <see cref="WindowTitle"/> exists: the
    /// title is a property of a window nobody can see, and drawing it is the only way it appears.
    /// </para>
    /// </remarks>
    public Control? Surface
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>The title to draw, when the root is a window, and nothing otherwise.</summary>
    public string? WindowTitle
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>The map between the two, in both directions.</summary>
    public XamlObjectMap? Objects => Session?.Objects;

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

    /// <summary>What the tab and the canvas label show.</summary>
    public string Title => IsDirty ? Name + " •" : Name;

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

        if (Root is Window window)
        {
            WindowTitle = window.Title is { Length: > 0 } title ? title : Name;

            object? content = window.Content;

            window.Content = null;

            Surface = content as Control;
        }
        else
        {
            WindowTitle = null;
            Surface = Root;
        }

        Raise(nameof(Objects));
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

        Surface = null;
        Root = null;
    }
}
