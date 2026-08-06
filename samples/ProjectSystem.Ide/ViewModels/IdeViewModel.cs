using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.MSBuild;
using Avalonia.Threading;

namespace ProjectSystem.Ide.ViewModels;

/// <summary>
/// An IDE's worth of state over one <see cref="ProjectWorkspace"/>.
/// </summary>
/// <remarks>
/// <para>
/// The sample exists to use the whole API rather than to be a good IDE, so it favours showing what
/// the model knows over hiding it. Every panel is a view of something a snapshot already says.
/// </para>
/// <para>
/// One rule runs through all of it: <b>the snapshot is the truth and nothing here caches a
/// derivative of it.</b> A publication rebuilds the tree, the details and the diagnostics from the
/// new snapshot, which is what makes the display incapable of disagreeing with the model.
/// </para>
/// </remarks>
public sealed partial class IdeViewModel : Observable, IDisposable
{
    private readonly ProjectWorkspace _workspace = new(new MSBuildProjectProvider());
    private readonly CancellationTokenSource _shutdown = new();

    private CanonicalPath _entryPoint;
    private bool _disposed;

    public IdeViewModel()
    {
        OpenCommand = new RelayCommand(() => Run(OpenAsync));
        RefreshCommand = new RelayCommand(() => Run(RefreshAsync), () => IsLoaded && !IsBusy);

        RestoreCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Restore)), CanOperate);
        BuildCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Build)), CanOperate);
        RebuildCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Rebuild)), CanOperate);
        CleanCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Clean)), CanOperate);

        InitialiseRun();

        SearchPackagesCommand = new RelayCommand(() => Run(SearchPackagesAsync), () => !IsBusy);
        InstallPackageCommand = new RelayCommand(() => Run(InstallPackageAsync), CanInstallPackage);
        UninstallPackageCommand = new RelayCommand(() => Run(UninstallPackageAsync), CanUninstallPackage);

        _workspace.SnapshotChanged += OnSnapshotChanged;

        Log("Ready. Open a .sln, .slnx or .csproj to begin.");
        Log(Environment());
    }

    public ObservableCollection<TreeNode> Tree { get; } = [];

    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];

    public ObservableCollection<string> Output { get; } = [];

    public RelayCommand OpenCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand BuildCommand { get; }

    public RelayCommand RebuildCommand { get; }

    public RelayCommand CleanCommand { get; }

    /// <summary>Set by the view, because picking a file needs a window to hang a dialog off.</summary>
    public Func<Task<string?>>? PickEntryPoint { get; set; }

    /// <summary>
    /// Opens an entry point given on the command line.
    /// </summary>
    /// <remarks>
    /// So the sample can be pointed at a solution without a dialog, which is what makes it usable
    /// as a smoke test: <c>ProjectSystem.Ide path\to\App.sln</c> loads it at startup and the output
    /// pane says what happened.
    /// </remarks>
    /// <param name="path">The solution or project to open.</param>
    /// <param name="thenRun">Whether to build and start it once it has loaded.</param>
    public void OpenAtStartup(string path, bool thenRun = false) => Run(async () =>
    {
        _entryPoint = CanonicalPath.Create(path);

        await LoadAsync();

        if (!thenRun || !IsLoaded)
        {
            return;
        }

        // The tree and the selection are built from the snapshot notification, which is posted
        // rather than raised inline -- publication happens off the UI thread and the handler has to
        // cross to it. Yielding to a lower priority lets that post run first, so there is something
        // selected to run.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        if (SelectedProject is null)
        {
            Log("! nothing runnable was selected after loading");

            return;
        }

        await RunProjectAsync();
    });

    public bool IsLoaded
    {
        get;
        private set => Set(ref field, value);
    }

    public bool IsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                RefreshAllCommands();
            }
        }
    }

    public string Status
    {
        get;
        private set => Set(ref field, value);
    } = "No workspace";

    public string VersionText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string Progress
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    public TreeNode? SelectedNode
    {
        get;
        set
        {
            if (Set(ref field, value) && value is not null)
            {
                Select(value);
            }
        }
    }

    /// <summary>
    /// Whichever tab the user last picked something in.
    /// </summary>
    /// <remarks>
    /// Two trees over one model, so the details panel follows whichever was touched last rather
    /// than belonging to either. Binding both to one selected-node property would have them clear
    /// each other, because a node of one tree is not in the other.
    /// </remarks>
    private TreeNode? Current
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                ShowSelection();
                RefreshAllCommands();
            }
        }
    }

    public ProjectDetails? Details
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>
    /// Stops the watcher, the feed, the load context and anything still running.
    /// </summary>
    /// <remarks>
    /// The workspace's own disposal waits for whatever is inside the mutation boundary, which is
    /// why it is asynchronous — and why this does not block the UI thread on it. The cancellation
    /// above has already told everything in flight to stop.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();

        StopProject();
        StopWatching();
        ReleaseXamlEnvironment();
        ReleaseFeed();

        _ = DisposeWorkspaceAsync();

        _shutdown.Dispose();
        _http.Dispose();
    }

    private async Task DisposeWorkspaceAsync()
    {
        try
        {
            await _workspace.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    /// <summary>
    /// Reports which MSBuild this process will use, without making it a condition of starting.
    /// </summary>
    /// <remarks>
    /// <see cref="MSBuildEnvironment.Register"/> throws when there is no SDK, and its own
    /// documentation says why that is right: it is an environment failure, and the provider turns it
    /// into a diagnostic so a consumer is told what is wrong rather than handed an exception. This
    /// runs in the constructor, where an exception would take the window with it — so the sample
    /// does what the library says a consumer should, and reports it.
    /// </remarks>
    private static string Environment()
    {
        try
        {
            return $"MSBuild: {MSBuildEnvironment.Register()}";
        }
        catch (InvalidOperationException exception)
        {
            return $"! No MSBuild: {exception.Message} Opening a project will report {MSBuildDiagnosticCodes.MSBuildNotFound}.";
        }
    }

    private bool CanOperate() => IsLoaded && !IsBusy;

    /// <summary>
    /// Runs a piece of work and keeps the window honest about it.
    /// </summary>
    /// <remarks>
    /// A cancellation is a normal end rather than an error: the window is closing. Anything else is
    /// shown, because a sample that swallowed a failure would be teaching the opposite of what this
    /// library is about.
    /// </remarks>
    private async void Run(Func<Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log($"! {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
            Progress = string.Empty;
        }
    }

    /// <summary>
    /// Runs work that keeps the display in step with the model, rather than work the user asked for.
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on <see cref="IsBusy"/>. Refreshing a panel is a consequence of a
    /// publication, and a publication happens <em>while</em> a load is still in flight — the load
    /// awaits the workspace, the UI thread goes free, and the snapshot notification arrives. Sending
    /// those through <see cref="Run"/> meant they were dropped exactly when they mattered, leaving
    /// one document's text beside another document's assemblies with nothing said about it.
    /// </remarks>
    /// <summary>
    /// A "newest wins" ticket for detached work, so a slower older pass cannot land on top of a
    /// newer one.
    /// </summary>
    /// <remarks>
    /// The busy flag used to serialise these by accident, and dropping the older one was its only
    /// virtue. Something has to keep the order once they are allowed to overlap: two publications,
    /// or two selections, each start a probe, and whichever file system answers first decides what
    /// is displayed. No interlocking — every issue and every check is on the UI thread, and only the
    /// awaited part is not.
    /// </remarks>
    private sealed class Latest
    {
        private long _issued;

        public long Take() => ++_issued;

        public bool IsCurrent(long ticket) => ticket == _issued;
    }

    private async void RunDetached(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log($"! {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task OpenAsync()
    {
        if (PickEntryPoint is null || await PickEntryPoint() is not { Length: > 0 } picked)
        {
            return;
        }

        _entryPoint = CanonicalPath.Create(picked);

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StopWatching();

        Log($"Opening {_entryPoint.FileName}…");

        WorkspaceLoadResult result = await _workspace.LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = _workspace.Identity,
                EntryPointPath = _entryPoint,

                // Items are what the solution explorer and the avares map are made of, so the
                // sample asks for them even though a tool that only wanted references need not.
                Options = new WorkspaceLoadOptions { IncludeItems = true },
            },
            _shutdown.Token);

        Report(result);
    }

    private async Task RefreshAsync()
    {
        Log("Refreshing…");

        Report(await _workspace.RefreshAsync(_shutdown.Token));
    }

    /// <summary>
    /// Shows what a load produced, including the shape the contract cares about most: a result whose
    /// status is computed from the evidence rather than declared beside it.
    /// </summary>
    private void Report(WorkspaceLoadResult result)
    {
        Log($"  {result.Status} — {Describe(result.Diagnostics.Length, "diagnostic")}");

        IsLoaded = result.Snapshot is not null;

        if (result.Snapshot is null)
        {
            Status = "Load failed";
        }

        ShowDiagnostics(result.Diagnostics);
        RefreshAllCommands();

        if (IsLoaded)
        {
            StartWatching();
        }
    }

    /// <summary>
    /// Raised after publication and outside the mutation gate, on whichever thread published — so
    /// the first thing it does is get onto the one that owns the controls.
    /// </summary>
    private void OnSnapshotChanged(object? sender, WorkspaceChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Show(e.Snapshot));

    private void Show(SolutionSnapshot snapshot)
    {
        VersionText = $"v{snapshot.Version}";

        Status = string.Create(
            CultureInfo.CurrentCulture,
            $"{snapshot.Name} — {Describe(snapshot.Projects.Length, "project")}" +
            $"{(snapshot.HasErrors ? ", with errors" : string.Empty)}");

        BuildTree(snapshot);
        BuildFileTree(snapshot);
        BuildResourceMap(snapshot);

        // No ShowSelection here: BuildTree assigns SelectedNode, whose setter already runs it. It
        // was called twice, and each pass rebuilds every fact about the project including the
        // runtime assembly closure.
    }

    /// <summary>
    /// Rebuilds the explorer from a snapshot: folders as the solution declared them, then projects,
    /// then the item groups a project evaluated to.
    /// </summary>
    private void BuildTree(SolutionSnapshot snapshot)
    {
        ProjectIdentity previous = SelectedNode?.Project ?? ProjectIdentity.None;

        Tree.Clear();

        var root = new TreeNode(TreeNodeKind.Solution, snapshot.Name, snapshot.EntryPoint.Path.FileName)
        {
            HasErrors = snapshot.HasErrors,
        };

        // Solution folders are a flat list carrying their path, so a tree is built from the paths
        // rather than from parent links -- see docs/limitations.md on why there are no links.
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (SolutionFolder folder in snapshot.Folders.OrderBy(static f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TreeNode(TreeNodeKind.Folder, folder.Name, folder.Path);

            folders[folder.Path] = node;

            Parent(folders, folder.Path, root).Children.Add(node);
        }

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            TreeNode host = snapshot.TryGetFolder(project.Identity, out SolutionFolder? folder)
                && folders.TryGetValue(folder.Path, out TreeNode? folderNode)
                    ? folderNode
                    : root;

            host.Children.Add(ProjectNode(project));
        }

        Tree.Add(root);

        // The first *project*, not the first child: a solution's root children are its folders, and
        // landing on one leaves every panel empty and Run with nothing to start.
        SelectedNode = Find(root, previous) ?? FirstProject(root) ?? root;
    }

    private static TreeNode ProjectNode(ProjectSnapshot project)
    {
        var node = new TreeNode(
            TreeNodeKind.Project,
            project.Name,
            project.ActiveTargetFramework ?? string.Join(", ", project.TargetFrameworks))
        {
            Project = project.Identity,
            File = project.ProjectFilePath,
            HasErrors = project.HasErrors,
        };

        foreach (IGrouping<string, ProjectItem> group in project.Items
            .GroupBy(static item => item.ItemType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupNode = new TreeNode(TreeNodeKind.Group, group.Key, group.Count().ToString(CultureInfo.CurrentCulture))
            {
                Project = project.Identity,
                IsExpanded = false,
            };

            foreach (ProjectItem item in group.Take(200))
            {
                groupNode.Children.Add(new TreeNode(TreeNodeKind.Item, item.Include)
                {
                    Project = project.Identity,
                    File = item.FullPath,
                });
            }

            node.Children.Add(groupNode);
        }

        return node;
    }

    private static TreeNode Parent(Dictionary<string, TreeNode> folders, string path, TreeNode root)
    {
        string trimmed = path.Trim('/');
        int slash = trimmed.LastIndexOf('/');

        return slash > 0 && folders.TryGetValue("/" + trimmed[..slash] + "/", out TreeNode? parent)
            ? parent
            : root;
    }

    /// <summary>The first project in tree order, wherever the solution's folders put it.</summary>
    private static TreeNode? FirstProject(TreeNode node)
    {
        if (node.Kind == TreeNodeKind.Project)
        {
            return node;
        }

        foreach (TreeNode child in node.Children)
        {
            if (FirstProject(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static TreeNode? Find(TreeNode node, ProjectIdentity project)
    {
        if (!project.IsEmpty && node.Kind == TreeNodeKind.Project && node.Project == project)
        {
            return node;
        }

        foreach (TreeNode child in node.Children)
        {
            if (Find(child, project) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Select(TreeNode node) => Current = node;

    private void ShowSelection()
    {
        SolutionSnapshot? snapshot = _workspace.CurrentSnapshot;

        if (snapshot is null || Current is null || Current.Project.IsEmpty)
        {
            Details = null;

            return;
        }

        if (snapshot.TryGetProject(Current.Project, out ProjectSnapshot? project))
        {
            Details = ProjectDetails.For(snapshot, project);
            ShowXamlFor(snapshot, project, Current.File);
        }
    }

    private void ShowDiagnostics(IEnumerable<ProjectDiagnostic> diagnostics)
    {
        Diagnostics.Clear();

        foreach (ProjectDiagnostic diagnostic in diagnostics)
        {
            Diagnostics.Add(DiagnosticRow.From(diagnostic));
        }
    }

    private void RefreshAllCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        RebuildCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        RunCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SearchPackagesCommand.RaiseCanExecuteChanged();
        InstallPackageCommand.RaiseCanExecuteChanged();
        UninstallPackageCommand.RaiseCanExecuteChanged();
    }

    private void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(message));

            return;
        }

        Output.Add(message);

        // Mirrored to standard output so the sample can be run headlessly and its behaviour read
        // from a log, which is what makes it usable as a smoke test rather than only as a demo.
        Console.WriteLine(message);

        while (Output.Count > 500)
        {
            Output.RemoveAt(0);
        }
    }

    private static string Describe(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
