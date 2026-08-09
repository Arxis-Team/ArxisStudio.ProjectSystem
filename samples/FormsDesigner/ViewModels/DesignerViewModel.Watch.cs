using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.ProjectSystem;
using Avalonia.Threading;

namespace FormsDesigner.ViewModels;

/// <summary>
/// Follows the project on disk, because this designer is not the only thing editing it.
/// </summary>
/// <remarks>
/// <para>
/// A form designer is used beside an IDE, not instead of one: the layout is done here and the code
/// is written there, and the same <c>.axaml</c> is open in both. A designer that only knew about its
/// own edits showed a form that had stopped being the file some time ago, and wrote over the other
/// tool's work the next time anybody pressed save.
/// </para>
/// <para>
/// Watching is composed by the host, which is what <c>ArxisStudio.ProjectSystem</c>'s ADR 0016 says
/// and what this is: a watcher, a debounce, and two answers — reload the form, or re-read the
/// project. The workspace is told nothing it could not be told by a person pressing refresh.
/// </para>
/// <para>
/// Editors do not write files the way this would expect. Rider writes a temporary file and renames
/// it over the original, so what arrives is a rename or a creation rather than a change, and a save
/// can produce several events for one edit. All of them are taken, coalesced over a quarter of a
/// second, and acted on once.
/// </para>
/// </remarks>
public sealed partial class DesignerViewModel
{
    private FileSystemWatcher? _watcher;

    /// <summary>Files that have changed and have not been dealt with yet.</summary>
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _settle;

    /// <summary>Whether the project needs re-reading rather than a form reloading.</summary>
    private bool _projectTouched;

    /// <summary>
    /// Starts watching the folder the project lives in.
    /// </summary>
    /// <remarks>
    /// The entry point's own directory, and everything under it. That is the project for a
    /// <c>.csproj</c> and the solution folder for a <c>.sln</c>, which is the same thing one level
    /// up — and either way it is where the files this designer opens are.
    /// </remarks>
    private void WatchProject()
    {
        StopWatching();

        if (EntryPoint.IsEmpty || !Directory.Exists(EntryPoint.Directory.Value))
        {
            return;
        }

        _settle ??= CreateSettleTimer();

        try
        {
            _watcher = new FileSystemWatcher(EntryPoint.Directory.Value)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Deleted += OnFileTouched;
            _watcher.Renamed += OnFileTouched;

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"! the project cannot be watched: {error.Message}");
        }
    }

    private DispatcherTimer CreateSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            RunDetached(SettleAsync);
        };

        return timer;
    }

    private void StopWatching()
    {
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();

            _watcher = null;
        }

        _settle?.Stop();
    }

    /// <summary>
    /// Notes what changed and waits for the writing to stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different facts arrive on this one event. A file whose <em>contents</em> changed matters
    /// only if this designer has it open. A file that <em>appeared, went away or was renamed</em>
    /// changes what the project consists of, whatever its extension is — an SDK project takes its
    /// items from globs, so a new class is a new item and the panel that lists them is stale until
    /// the evaluation is done again. That is why a created <c>.cs</c> counts and a saved one does
    /// not: the second changes no glob.
    /// </para>
    /// <para>
    /// The noise is filtered out first. Builds write under <c>bin</c> and <c>obj</c>, tools keep
    /// their state in dot-directories, and editors save through temporary files whose names they do
    /// not intend anybody to see — re-evaluating a project for any of those would be a second of
    /// nothing, repeatedly.
    /// </para>
    /// </remarks>
    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        if (IsNoise(e.FullPath))
        {
            return;
        }

        bool structural = e.ChangeType is not WatcherChangeTypes.Changed;

        string[] paths = e is RenamedEventArgs renamed
            ? [renamed.FullPath, renamed.OldFullPath]
            : [e.FullPath];

        Dispatcher.UIThread.Post(() =>
        {
            var noted = false;

            foreach (string path in paths)
            {
                if (IsMarkupPath(path))
                {
                    _touched.Add(path);

                    noted = true;
                }
            }

            // Appearing and disappearing is the project's business; a save is not.
            if (structural)
            {
                _projectTouched = true;

                noted = true;
            }

            if (!noted)
            {
                return;
            }

            _settle?.Stop();
            _settle?.Start();
        });
    }

    /// <summary>What gets written under a project that nobody is editing.</summary>
    private static bool IsNoise(string path)
    {
        foreach (string part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part is "bin" or "obj" || (part.Length > 1 && part[0] == '.'))
            {
                return true;
            }
        }

        string name = Path.GetFileName(path);

        // JetBrains saves through these two, and every editor saves through something.
        return name.Contains("___jb_", StringComparison.Ordinal)
            || name.EndsWith('~')
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMarkupPath(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".AXAML" or ".XAML";

    /// <summary>
    /// Deals with everything that changed while the writing was going on.
    /// </summary>
    /// <remarks>
    /// A project file means the evaluation is stale — items, references, the lot — so the workspace
    /// is re-read. A document means whatever is open on it is showing something else, so it is
    /// reloaded. Nothing is done about a document nobody has open: the project panel lists files
    /// from the snapshot, which the refresh above brings up to date.
    /// </remarks>
    private async Task SettleAsync()
    {
        bool project = _projectTouched;
        string[] documents = [.. _touched];

        _projectTouched = false;
        _touched.Clear();

        foreach (string path in documents)
        {
            if (File.Exists(path))
            {
                await ReloadIfOpenAsync(path);
            }
            else
            {
                CloseIfOpen(path);
            }
        }

        if (project && IsLoaded)
        {
            Log("The project changed on disk — re-reading it.");

            await _workspace.RefreshAsync(_shutdown.Token);
        }
    }

    /// <summary>
    /// Closes a form whose file has gone.
    /// </summary>
    /// <remarks>
    /// Deleted in the other editor, or renamed, which arrives as the same thing. A tab editing a
    /// document with nowhere to save to is worse than no tab: the next save would put the file back,
    /// which is not what deleting it meant. Unsaved edits go with it — there is nothing left to
    /// reconcile them against — and the console says so, because a tab that closes itself without a
    /// word looks like a crash.
    /// </remarks>
    private void CloseIfOpen(string path)
    {
        if (!CanonicalPath.TryCreate(path, out CanonicalPath file)
            || Forms.FirstOrDefault(open => open.File == file) is not { } form)
        {
            return;
        }

        Log($"{form.Name} was deleted outside — closing it"
            + (form.IsDirty ? ", with unsaved edits" : string.Empty));

        CloseForm(form);
    }

    /// <summary>
    /// Brings an open form back in line with its file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied as a document update rather than by reopening the form, so the session, the live tree
    /// and the canvas survive: the selection is put back by path, the tabs do not move, and the
    /// change joins the undo history like any other — which means an edit made in another editor can
    /// be taken back here as well.
    /// </para>
    /// <para>
    /// A form with unsaved edits is not overwritten. Whoever is typing here has work that is not in
    /// the file, and throwing it away because another program wrote to the same path is the one
    /// thing a designer must not do. It says so instead, and the next save is the person's decision.
    /// </para>
    /// </remarks>
    private async Task ReloadIfOpenAsync(string path)
    {
        if (!CanonicalPath.TryCreate(path, out CanonicalPath file)
            || Forms.FirstOrDefault(open => open.File == file) is not { } form)
        {
            return;
        }

        string text;

        try
        {
            text = await ReadAsync(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file being written by another process is a file to look at again in a moment, not an
            // error: the watcher will bring the next event along with it.
            return;
        }

        if (form.Document?.SourceText.ToString() == text)
        {
            // Our own save, or a write that changed nothing. Either way there is nothing to show.
            return;
        }

        if (form.IsDirty)
        {
            Log($"! {form.Name} changed on disk and has unsaved edits here — the edits are kept");

            return;
        }

        await ApplyDocumentAsync(
            form,
            XamlDocument.Parse(text, new XamlParseOptions { DocumentUri = form.Document?.Uri }),
            "reload");

        // It is the file again, whatever the update thought.
        form.MarkSaved();

        Log($"{form.Name} was changed outside — reloaded.");
    }
}
