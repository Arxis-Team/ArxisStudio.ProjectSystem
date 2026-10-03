using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// The source watcher against a real file system: what an editor's save, a move and a rename look
/// like once they have been through it and through the coalescer a host puts after it.
/// </summary>
/// <remarks>
/// Everything decidable without a disk is in <see cref="SourceWatchPlanTests"/>. These wait on a
/// <see cref="TaskCompletionSource"/> that the last expected notification completes, as
/// <see cref="ProjectFileWatcherTests"/> do: nothing sleeps, and a notification that never arrives
/// hangs until the framework's token cancels the test.
/// </remarks>
public sealed class ProjectSourceWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arxis-source-" + Guid.NewGuid().ToString("N"));

    public ProjectSourceWatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App", "Views"));
        Directory.CreateDirectory(Path.Combine(_root, "App", "Pages"));
        File.WriteAllText(Path.Combine(_root, "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "App", "Views", "MainWindow.axaml"), "<Window />");
    }

    // How long a notification may take before its absence is a failure rather than a hang. The
    // framework cancels the test's token when it passes, and every wait here observes that token.
    private const int Patience = 30_000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private CanonicalPath Form => At("App", "Views", "MainWindow.axaml");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher on the way down can hold a handle for a moment.
        }
    }

    [Fact(Timeout = Patience)]
    public async Task AnAtomicSave_IsOneChangeOfTheSavedFile()
    {
        // JetBrains Rider's safe write: the text to a temporary file, the original renamed aside,
        // the temporary renamed over it, the original deleted.
        string temporary = Form.Value + "___jb_tmp___";
        string aside = Form.Value + "___jb_old___";

        var recorder = new Recorder(change =>
            change.Kind == FileChangeKind.Deleted && change.Path == CanonicalPath.Create(aside));

        using var watcher = new ProjectSourceWatcher(recorder.Add);
        watcher.Watch(Solution());

        await File.WriteAllTextAsync(temporary, "<Window Title=\"Saved\" />", Token);
        File.Move(Form.Value, aside);
        File.Move(temporary, Form.Value);
        File.Delete(aside);

        await recorder.Done.WaitAsync(Token);

        Assert.Equal([new FileChange(Form, FileChangeKind.Changed)], recorder.Netted());
    }

    [Fact(Timeout = Patience)]
    public async Task AFileMovedBetweenFoldersOfAProject_IsARename()
    {
        // The operating system reports a deletion and a creation; the coalescer joins them.
        CanonicalPath moved = At("App", "Pages", "MainWindow.axaml");
        var recorder = new Recorder(change => change.Path == moved);

        using var watcher = new ProjectSourceWatcher(recorder.Add);
        watcher.Watch(Solution());

        File.Move(Form.Value, moved.Value);

        await recorder.Done.WaitAsync(Token);

        Assert.Equal([FileChange.Renamed(Form, moved)], recorder.Netted());
    }

    [Fact(Timeout = Patience)]
    public async Task AFolderRenamed_IsOneRenameOfTheFolder()
    {
        CanonicalPath views = At("App", "Views");
        CanonicalPath shell = At("App", "Shell");
        var recorder = new Recorder(change => change.Path == shell);

        using var watcher = new ProjectSourceWatcher(recorder.Add);
        watcher.Watch(Solution());

        Directory.Move(views.Value, shell.Value);

        await recorder.Done.WaitAsync(Token);

        Assert.Equal([FileChange.Renamed(views, shell)], recorder.Netted());
    }

    [Fact(Timeout = Patience)]
    public async Task AFileCreatedAndAnotherDeleted_ComeWithTheirKinds()
    {
        CanonicalPath created = At("App", "Views", "Settings.axaml");
        var recorder = new Recorder(change => change.Kind == FileChangeKind.Deleted && change.Path == Form);

        using var watcher = new ProjectSourceWatcher(recorder.Add);
        watcher.Watch(Solution());

        await File.WriteAllTextAsync(created.Value, "<UserControl />", Token);
        File.Delete(Form.Value);

        await recorder.Done.WaitAsync(Token);

        Assert.Equal(
            [new FileChange(created, FileChangeKind.Created), new FileChange(Form, FileChangeKind.Deleted)],
            recorder.Netted());
    }

    [Fact]
    public void FromRename_AHalfReportedRename_IsTheHalfThatWasSeen()
    {
        // .NET reports an old name with no new one, or the reverse, with an empty name — and the full
        // path built from an empty name is the watched directory itself.
        string views = At("App", "Views").Value;
        CanonicalPath settings = At("App", "Views", "Settings.axaml");

        Assert.Equal(
            FileChange.Renamed(Form, settings),
            ProjectSourceWatcher.FromRename(new RenamedEventArgs(WatcherChangeTypes.Renamed, views, "Settings.axaml", "MainWindow.axaml")));
        Assert.Equal(
            new FileChange(settings, FileChangeKind.Created),
            ProjectSourceWatcher.FromRename(new RenamedEventArgs(WatcherChangeTypes.Renamed, views, "Settings.axaml", string.Empty)));
        Assert.Equal(
            new FileChange(Form, FileChangeKind.Deleted),
            ProjectSourceWatcher.FromRename(new RenamedEventArgs(WatcherChangeTypes.Renamed, views, string.Empty, "MainWindow.axaml")));
        Assert.Null(
            ProjectSourceWatcher.FromRename(new RenamedEventArgs(WatcherChangeTypes.Renamed, views, string.Empty, string.Empty)));
    }

    [Fact]
    public void Watch_TheSameSolutionAgain_KeepsTheWatchesItHas()
    {
        using var watcher = new ProjectSourceWatcher(static _ => { });

        watcher.Watch(Solution());

        int started = watcher.Started;
        int watching = watcher.Watching;

        // A host calls this after every snapshot; a watch replaced each time had a gap nothing heard.
        watcher.Watch(Solution());

        Assert.True(watching > 0);
        Assert.Equal(started, watcher.Started);
        Assert.Equal(watching, watcher.Watching);
    }

    [Fact]
    public void Watch_AProjectThatWentAway_StopsBeingWatchedAndTheRestIsKept()
    {
        using var watcher = new ProjectSourceWatcher(static _ => { });

        watcher.Watch(Solution(withLibrary: true));

        int started = watcher.Started;
        int watching = watcher.Watching;

        watcher.Watch(Solution());

        Assert.Equal(watching - 1, watcher.Watching);
        Assert.Equal(started, watcher.Started);

        // And back: only the directory that returned is started.
        watcher.Watch(Solution(withLibrary: true));

        Assert.Equal(watching, watcher.Watching);
        Assert.Equal(started + 1, watcher.Started);
    }

    [Fact(Timeout = Patience)]
    public async Task Watch_Again_StillHearsAChangeOnce()
    {
        CanonicalPath created = At("App", "Views", "Settings.axaml");
        var recorder = new Recorder(change => change.Path == created);

        using var watcher = new ProjectSourceWatcher(recorder.Add);

        watcher.Watch(Solution());
        watcher.Watch(Solution());

        await File.WriteAllTextAsync(created.Value, "<UserControl />", Token);

        await recorder.Done.WaitAsync(Token);

        Assert.Equal([new FileChange(created, FileChangeKind.Created)], recorder.Netted());
    }

    [Fact]
    public void AfterDisposal_WatchingThrows()
    {
        var watcher = new ProjectSourceWatcher(static _ => { });
        watcher.Watch(Solution());
        watcher.Dispose();
        watcher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => watcher.Watch(Solution()));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectSourceWatcher(null!));

        using var watcher = new ProjectSourceWatcher(static _ => { });

        Assert.Throws<ArgumentNullException>(() => watcher.Watch(null!));
    }

    private CanonicalPath At(params string[] segments) => CanonicalPath.Create(Path.Combine([_root, .. segments]));

    private SolutionSnapshot Solution(bool withLibrary = false)
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        CanonicalPath projectFile = At("App", "App.csproj");

        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, projectFile),
            Name = "App",
            ProjectFilePath = projectFile,
        };

        project.Items.Add(new ProjectItem { ItemType = "AvaloniaXaml", Include = "Views/MainWindow.axaml", FullPath = Form });

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest { Workspace = workspace, EntryPointPath = projectFile },
        };

        solution.Projects.Add(project.ToSnapshot());

        if (withLibrary)
        {
            CanonicalPath libraryFile = At("Lib", "Lib.csproj");

            Directory.CreateDirectory(libraryFile.Directory.Value);
            File.WriteAllText(libraryFile.Value, "<Project />");

            solution.Projects.Add(new ProjectSnapshotBuilder
            {
                Identity = ProjectIdentity.Create(workspace, libraryFile),
                Name = "Lib",
                ProjectFilePath = libraryFile,
            }.ToSnapshot());
        }

        return solution.ToSnapshot();
    }

    /// <summary>
    /// Keeps what the watcher reported, says when the notification a test waits for has arrived, and
    /// nets it all the way a host's coalescer would.
    /// </summary>
    private sealed class Recorder(Func<FileChange, bool> last)
    {
        // Long enough that only Flush delivers: the batch is everything recorded, never a part of it.
        private static readonly FileChangeCoalescingOptions Never = new()
        {
            QuietPeriod = TimeSpan.FromHours(1),
            MaximumDelay = TimeSpan.FromHours(1),
        };

        private readonly List<FileChange> _seen = [];
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Done => _done.Task;

        public void Add(FileChange change)
        {
            lock (_seen)
            {
                _seen.Add(change);
            }

            if (last(change))
            {
                _done.TrySetResult();
            }
        }

        public ImmutableArray<FileChange> Netted()
        {
            var batches = new List<ImmutableArray<FileChange>>();

            using (FileChangeCoalescer coalescer = FileChangeCoalescer.ForChanges(batches.Add, Never))
            {
                lock (_seen)
                {
                    foreach (FileChange change in _seen)
                    {
                        coalescer.Add(change);
                    }
                }

                coalescer.Flush();
            }

            // A folder's last-write time moves when a file in it changes. That is the listing being
            // written, not a change anybody asked about, and Classify finds nothing in it either.
            return [.. batches.SelectMany(static batch => batch)
                .Where(static change => !(change.Kind == FileChangeKind.Changed && Directory.Exists(change.Path.Value)))];
        }
    }
}
