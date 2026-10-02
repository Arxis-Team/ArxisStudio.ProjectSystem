using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>The documents: opened, attached to the generation, followed, and kept registered for placed controls.</summary>
public sealed partial class ProjectDesignHost
{
    private readonly List<DesignDocument> _documents = [];

    /// <summary>Windows the replaced sessions built, closed once whoever showed them let go.</summary>
    private readonly List<Window> _retiredRoots = [];

    /// <summary>The documents somebody is looking at, or <see langword="null"/> for every one.</summary>
    private HashSet<XamlLiveDocument>? _visible;

    /// <summary>Gets the documents the host opened and has not closed, in the order they were opened.</summary>
    public ImmutableArray<XamlLiveDocument> Documents
    {
        get
        {
            lock (_sync)
            {
                return [.. _documents.Select(static document => document.Live)];
            }
        }
    }

    /// <summary>Finds where a document's file is now — it follows the file when somebody else moves it.</summary>
    /// <param name="document">A document the host opened.</param>
    /// <param name="file">Where its file is, when the host opened it.</param>
    /// <returns><see langword="true"/> when the host opened the document and has not closed it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public bool TryGetFile(XamlLiveDocument document, out CanonicalPath file)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_sync)
        {
            file = Find(document)?.File ?? default;

            return !file.IsEmpty;
        }
    }

    /// <summary>Opens a project's document and shows it in the live generation.</summary>
    /// <remarks>
    /// <para>
    /// The document gets the history of its own (Markup ADR 0025), its <c>avares</c> URI from the
    /// snapshot, and the environment of its project — the types the project's build sees. It is attached
    /// at once when a generation is live; when one is being replaced, opening waits for the successor;
    /// when the host requires a restart, it opens detached, with its text and nothing built from it.
    /// </para>
    /// <para>
    /// A file already open answers with its document, and the options are not consulted. A window the
    /// document's session builds is the host's to close: on every replacement of the session, and when
    /// the document is closed.
    /// </para>
    /// </remarks>
    /// <param name="file">The document's file, which a project of the snapshot claims.</param>
    /// <param name="options">How to open it, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentException"><paramref name="file"/> is empty, or no project of the snapshot claims it.</exception>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async ValueTask<XamlLiveDocument> OpenDocumentAsync(
        CanonicalPath file,
        ProjectDesignDocumentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (file.IsEmpty)
        {
            throw new ArgumentException("A document is opened from a file.", nameof(file));
        }

        options ??= ProjectDesignDocumentOptions.Default;

        SolutionSnapshot snapshot = StartedSnapshot();

        if (!snapshot.TryGetProjectForFile(file, out ProjectSnapshot? project))
        {
            throw new ArgumentException($"'{file}' belongs to no project of '{snapshot.Name}'.", nameof(file));
        }

        lock (_sync)
        {
            if (FindFile(file) is { } open)
            {
                return open.Live;
            }
        }

        SourceText? disk = options.Text is null || options.SavedText is null
            ? await ReadTextAsync(file, cancellationToken).ConfigureAwait(false)
            : null;

        SourceText text = options.Text is { } restored ? SourceText.From(restored, disk?.Encoding, disk?.HasByteOrderMark ?? false) : disk!;
        SourceText saved = options.SavedText is { } wasSaved ? SourceText.From(wasSaved, text.Encoding, text.HasByteOrderMark) : disk!;

        XamlLiveDocument live = XamlLiveDocument.Open(UriOf(snapshot, file), text, saved);
        var document = new DesignDocument(this, live, file, project.Identity, options.RootAccess);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DesignDocument? already;

            lock (_sync)
            {
                already = Volatile.Read(ref _disposed) == 0 ? FindFile(file) : null;

                if (already is null && Volatile.Read(ref _disposed) == 0)
                {
                    _documents.Add(document);
                    document.Subscribe();
                }
            }

            if (already is not null || Volatile.Read(ref _disposed) != 0)
            {
                await live.DisposeAsync().ConfigureAwait(false);

                ThrowIfDisposed();

                return already!.Live;
            }

            await AttachAsync(document, cancellationToken).ConfigureAwait(false);
            await RegisterAsync(live.Document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }

        return live;
    }

    /// <summary>Closes a document the host opened, disposing its session and closing a window it built.</summary>
    /// <remarks>
    /// Take the root off whatever shows it first: the session is disposed, and nothing is raised for it.
    /// Unsaved edits go with the document. Placed copies of its control follow the file again.
    /// </remarks>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the document is closed; at once for one the host does not have.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public async ValueTask CloseDocumentAsync(XamlLiveDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DesignDocument? closing;

            lock (_sync)
            {
                closing = Find(document);

                if (closing is not null)
                {
                    _documents.Remove(closing);
                    _visible?.Remove(document);
                }
            }

            if (closing is null)
            {
                return;
            }

            // Placed copies showed the edits nobody saved; from now on they show the file.
            bool unsaved = closing.Live.IsDirty;

            await DisposeDocumentAsync(closing).ConfigureAwait(false);

            if (unsaved
                && _workspace.CurrentSnapshot is { } snapshot
                && await RegisterFromDiskAsync(snapshot, closing.File, cancellationToken).ConfigureAwait(false) is { } fromDisk)
            {
                await RefreshDependentsAsync(fromDisk, except: null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Says which documents somebody is looking at.</summary>
    /// <remarks>
    /// A swap attaches only these to the successor, and a change to a control they place rebuilds only
    /// these; the rest are detached or marked, and catch up in <see cref="EnsureLiveAsync"/> when they are
    /// shown. Until this is called every document counts as looked at. Documents the host did not open
    /// are ignored.
    /// </remarks>
    /// <param name="documents">The documents shown, or <see langword="null"/> for every one.</param>
    public void SetVisibleDocuments(IEnumerable<XamlLiveDocument>? documents)
    {
        lock (_sync)
        {
            _visible = documents is null ? null : [.. documents.Where(document => Find(document) is not null)];
        }
    }

    /// <summary>
    /// Brings a document that is about to be shown up to date: attached to the live generation, and
    /// rebuilt when a control it places changed while nobody looked.
    /// </summary>
    /// <param name="document">A document the host opened.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the document shows what it says; waits for a swap in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The host did not open the document, or has closed it.</exception>
    public async ValueTask EnsureLiveAsync(XamlLiveDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DesignDocument shown;
            bool attached;
            bool stale;

            lock (_sync)
            {
                shown = Find(document) ?? throw new ArgumentException("The design host did not open this document, or has closed it.", nameof(document));
                attached = shown.Attached;
                stale = shown.Stale;
                shown.Stale = false;
            }

            if (!attached)
            {
                await AttachAsync(shown, cancellationToken).ConfigureAwait(false);
            }
            else if (stale)
            {
                await document.RebuildAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Reads a document's file again and takes what it says, unless the document has edits the file does not.</summary>
    /// <remarks>
    /// Taken text is a step of the document's history, undone like any other. Over unsaved edits nothing
    /// changes: the result says <see cref="XamlExternalTextOutcome.Conflict"/>, and
    /// <see cref="ExternalConflict"/> is raised before this returns, for the person to decide. What a
    /// restored document is opened with is reconciled with its file this way.
    /// </remarks>
    /// <param name="document">A document the host opened.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What became of the file's text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The host did not open the document, or has closed it.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async ValueTask<XamlExternalTextResult> ReloadAsync(XamlLiveDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        DesignDocument reloading;

        lock (_sync)
        {
            reloading = Find(document) ?? throw new ArgumentException("The design host did not open this document, or has closed it.", nameof(document));
        }

        SourceText text = await ReadTextAsync(reloading.File, cancellationToken).ConfigureAwait(false);

        return await TakeExternalTextAsync(reloading, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a document's text to its file, in the encoding it was read in, and records it as saved.</summary>
    /// <remarks>
    /// The write comes back through the owner's watcher as a change to the file, and the document
    /// answers it as its own save — nothing is taken twice and nothing is a conflict.
    /// </remarks>
    /// <param name="document">A document the host opened.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the file is written and the document knows it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The host did not open the document, or has closed it.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async ValueTask SaveAsync(XamlLiveDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        CanonicalPath file;

        lock (_sync)
        {
            file = (Find(document) ?? throw new ArgumentException("The design host did not open this document, or has closed it.", nameof(document))).File;
        }

        SourceText written = document.Document.SourceText;
        byte[] preamble = written.HasByteOrderMark ? written.Encoding.GetPreamble() : [];
        byte[] body = written.Encoding.GetBytes(written.ToString());

        FileStream stream = new(file.Value, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await document.MarkSavedAsync(written, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attaches a document to the live generation, when there is one. Inside a turn.</summary>
    private async Task AttachAsync(DesignDocument document, CancellationToken cancellationToken)
    {
        if (LoadingOf(document) is not { } loading)
        {
            return;
        }

        await document.Live.AttachAsync(loading.Environment, loading.Options, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            document.Attached = true;
            document.Stale = false;
        }
    }

    /// <summary>Detaches every document, keeping text and history, so nothing of them holds the generation.</summary>
    private async Task DetachDocumentsAsync(CancellationToken cancellationToken)
    {
        DesignDocument[] documents;

        lock (_sync)
        {
            documents = [.. _documents];
        }

        foreach (DesignDocument document in documents)
        {
            await document.Live.DetachAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                document.Attached = false;
                document.Stale = false;
            }
        }
    }

    /// <summary>Attaches the documents somebody is looking at to the live generation.</summary>
    private async Task AttachVisibleDocumentsAsync(CancellationToken cancellationToken)
    {
        DesignDocument[] documents;

        lock (_sync)
        {
            documents = [.. _documents.Where(IsVisible)];
        }

        foreach (DesignDocument document in documents)
        {
            await AttachAsync(document, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes a document and closes a window its session built.</summary>
    private static async Task DisposeDocumentAsync(DesignDocument document)
    {
        document.Unsubscribe();

        object? root = document.Live.Session?.RootObject;

        await document.Live.DisposeAsync().ConfigureAwait(false);

        if (root is Window window)
        {
            await Dispatcher.UIThread.InvokeAsync(() => CloseQuietly(window)).GetTask().ConfigureAwait(false);
        }
    }

    /// <summary>Takes text from outside into a document, raising a conflict instead of overwriting edits.</summary>
    private async Task<XamlExternalTextResult> TakeExternalTextAsync(
        DesignDocument document, SourceText text, CancellationToken cancellationToken)
    {
        XamlExternalTextResult result = await document.Live.AcceptExternalTextAsync(
            text, _options.ExternalEditDescription, XamlExternalTextPolicy.ApplyIfClean, cancellationToken).ConfigureAwait(false);

        if (result.Outcome == XamlExternalTextOutcome.Conflict)
        {
            CanonicalPath file;

            lock (_sync)
            {
                file = document.File;
            }

            await RaiseAsync(ExternalConflict, new ProjectDesignConflictEventArgs(document.Live, file, text.ToString())).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Registers every document of the design set for placed controls — from disk, then the open ones,
    /// whose text outranks their files.
    /// </summary>
    private async Task RegisterDocumentsAsync(SolutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        HashSet<ProjectIdentity> set;
        HashSet<CanonicalPath> open;
        DesignDocument[] documents;

        lock (_sync)
        {
            if (_population is null)
            {
                return;
            }

            set = [.. _designSet];
            documents = [.. _documents];
            open = [.. documents.Select(static document => document.File)];
        }

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            if (!set.Contains(project.Identity))
            {
                continue;
            }

            foreach (ProjectItem item in project.Items)
            {
                // A linked document is registered by the project it lives in, not by every one that links it.
                if (IsMarkupItem(item)
                    && !item.FullPath.IsEmpty
                    && item.FullPath.StartsWith(project.ProjectDirectory)
                    && !open.Contains(item.FullPath))
                {
                    await RegisterFromDiskAsync(snapshot, item.FullPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        foreach (DesignDocument document in documents)
        {
            await RegisterAsync(document.Live.Document, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a file and registers it for placed controls, answering the document when it is a control's.</summary>
    private async Task<XamlDocument?> RegisterFromDiskAsync(SolutionSnapshot snapshot, CanonicalPath file, CancellationToken cancellationToken)
    {
        SourceText text;

        try
        {
            text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mid-write, or gone: the next change to it brings it along, and the compiled markup covers
            // the meantime.
            return null;
        }

        XamlDocument document = XamlDocument.Parse(text, new XamlParseOptions { DocumentUri = UriOf(snapshot, file) });

        return await RegisterAsync(document, cancellationToken).ConfigureAwait(false) ? document : null;
    }

    /// <summary>Registers a document for placed controls, answering whether it is a control's.</summary>
    private async Task<bool> RegisterAsync(XamlDocument document, CancellationToken cancellationToken)
    {
        ProjectXamlPopulation? population;

        lock (_sync)
        {
            population = _population;
        }

        if (population is null)
        {
            return false;
        }

        try
        {
            return await population.SetDocumentAsync(document, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (ObjectDisposedException)
        {
            // The generation is being replaced, and its successor registers every document again.
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the documents somebody is looking at that place a control whose document changed, and
    /// marks the rest for when they are shown.
    /// </summary>
    /// <remarks>
    /// Population changes constructions, not instances on screen, so a form placing the control is built
    /// again to show it. A form places the control when an element is named like its class, in its CLR
    /// namespace — or in a namespace URI that may map to it, which only the types could rule out.
    /// </remarks>
    private async Task RefreshDependentsAsync(XamlDocument changed, DesignDocument? except, CancellationToken cancellationToken)
    {
        if (changed.Root?.GetDirective(XamlDirectives.Class) is not { Length: > 0 } className)
        {
            return;
        }

        int dot = className.LastIndexOf('.');
        string clrNamespace = dot < 0 ? string.Empty : className[..dot];
        string simpleName = className[(dot + 1)..];

        var rebuild = new List<XamlLiveDocument>();

        lock (_sync)
        {
            foreach (DesignDocument document in _documents)
            {
                if (ReferenceEquals(document, except) || !Places(document.Live.Document, clrNamespace, simpleName))
                {
                    continue;
                }

                if (document.Attached && IsVisible(document))
                {
                    rebuild.Add(document.Live);
                }
                else
                {
                    document.Stale = true;
                }
            }
        }

        foreach (XamlLiveDocument document in rebuild)
        {
            await document.RebuildAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a document has an element that may be the control.</summary>
    private static bool Places(XamlDocument document, string clrNamespace, string simpleName)
    {
        if (document.Root is not { } root)
        {
            return false;
        }

        foreach (XamlElement element in root.DescendantElements().Prepend(root))
        {
            if (!string.Equals(element.Name.LocalName, simpleName, StringComparison.Ordinal))
            {
                continue;
            }

            if (ClrNamespaceOf(element.NamespaceUri) is not { } written
                || string.Equals(written, clrNamespace, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The CLR namespace a <c>using:</c> or <c>clr-namespace:</c> URI names; nothing for any other.</summary>
    private static string? ClrNamespaceOf(string? namespaceUri)
    {
        const string Using = "using:";
        const string ClrNamespace = "clr-namespace:";

        if (namespaceUri is null)
        {
            return null;
        }

        if (namespaceUri.StartsWith(Using, StringComparison.Ordinal))
        {
            return namespaceUri[Using.Length..];
        }

        if (namespaceUri.StartsWith(ClrNamespace, StringComparison.Ordinal))
        {
            string rest = namespaceUri[ClrNamespace.Length..];
            int semicolon = rest.IndexOf(';', StringComparison.Ordinal);

            return semicolon < 0 ? rest : rest[..semicolon];
        }

        return null;
    }

    /// <summary>Whether somebody is looking at a document. Under the lock.</summary>
    private bool IsVisible(DesignDocument document) => _visible is null || _visible.Contains(document.Live);

    /// <summary>The host's record of a document. Under the lock.</summary>
    private DesignDocument? Find(XamlLiveDocument document) =>
        _documents.FirstOrDefault(open => ReferenceEquals(open.Live, document));

    /// <summary>The host's record of the document open on a file. Under the lock.</summary>
    private DesignDocument? FindFile(CanonicalPath file) =>
        _documents.FirstOrDefault(open => open.File == file);

    /// <summary>The documents open on a file or below a folder. Under the lock.</summary>
    private DesignDocument[] FindAtOrBelow(CanonicalPath path) =>
        [.. _documents.Where(open => open.File.StartsWith(path))];

    /// <summary>The <c>avares</c> URI of a file, or its own path when no project makes one of it.</summary>
    private static Uri UriOf(SolutionSnapshot snapshot, CanonicalPath file) =>
        ProjectResourceMap.UriOf(snapshot, file) ?? new Uri(file.Value);

    /// <summary>Reads a file as the document's text, keeping its encoding and byte-order mark for the save.</summary>
    private static async Task<SourceText> ReadTextAsync(CanonicalPath file, CancellationToken cancellationToken)
    {
        FileStream stream = new(file.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            return await SourceText.FromAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The snapshot a started host works against.</summary>
    private SolutionSnapshot StartedSnapshot()
    {
        lock (_sync)
        {
            if (!_started)
            {
                throw new InvalidOperationException("Start the design host before opening documents in it.");
            }
        }

        return _workspace.CurrentSnapshot ?? throw new InvalidOperationException("The workspace has no snapshot.");
    }

    /// <summary>
    /// Queues a window a replaced session built for closing, once the handlers that take it off the
    /// screen have run.
    /// </summary>
    /// <remarks>
    /// A window-rooted form's root is a real <c>Window</c>, never shown, and the windowing platform holds
    /// it until it is closed — with it every type of its generation. Nothing else closes it: the session's
    /// objects are the host's.
    /// </remarks>
    private void OnSessionReplaced(object? sender, XamlSessionReplacedEventArgs e)
    {
        if (e.Previous?.RootObject is Window window && !ReferenceEquals(window, e.Current?.RootObject))
        {
            lock (_sync)
            {
                _retiredRoots.Add(window);
            }

            Dispatcher.UIThread.Post(CloseRetiredRoots, DispatcherPriority.Background);
        }
    }

    /// <summary>Closes the windows replaced sessions built. On the user interface thread.</summary>
    private void CloseRetiredRoots()
    {
        Window[] windows;

        lock (_sync)
        {
            windows = [.. _retiredRoots];
            _retiredRoots.Clear();
        }

        foreach (Window window in windows)
        {
            CloseQuietly(window);
        }
    }

    private static void CloseQuietly(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            // A window that will not close is a window the platform still holds, and the reclaim a swap
            // asks for turns that into a restart rather than a guess.
        }
    }

    /// <summary>The host's record of a document it opened. Its mutable state is read and written under the host's lock.</summary>
    private sealed class DesignDocument(
        ProjectDesignHost host,
        XamlLiveDocument live,
        CanonicalPath file,
        ProjectIdentity project,
        IXamlRootAccess? rootAccess)
    {
        public XamlLiveDocument Live { get; } = live;

        public CanonicalPath File { get; set; } = file;

        public ProjectIdentity Project { get; set; } = project;

        public IXamlRootAccess? RootAccess { get; } = rootAccess;

        /// <summary>Whether the document is attached to the live generation.</summary>
        public bool Attached { get; set; }

        /// <summary>Whether a control it places changed while it was not rebuilt for it.</summary>
        public bool Stale { get; set; }

        public void Subscribe()
        {
            Live.SessionReplaced += host.OnSessionReplaced;
            Live.Changed += OnChanged;
        }

        public void Unsubscribe()
        {
            Live.SessionReplaced -= host.OnSessionReplaced;
            Live.Changed -= OnChanged;
        }

        private void OnChanged(object? sender, XamlLiveDocumentChangedEventArgs e)
        {
            if ((e.Changes & XamlLiveDocumentChanges.Text) != 0)
            {
                host.Enqueue(new Work.Edited(this));
            }
        }
    }
}
