using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.MSBuild;
using Avalonia.Threading;

namespace FormsDesigner.ViewModels;

/// <summary>
/// A visual form designer over three libraries that know nothing about each other.
/// </summary>
/// <remarks>
/// <para>
/// The division is the whole point, and it is worth stating before any of the code below makes
/// sense:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>ArxisStudio.ProjectSystem</b> says what project is open, which files are in it, what it
/// resolves to, and how to restore, build and run it. It never looks inside a document.
/// </item>
/// <item>
/// <b>ArxisStudio.Markup</b> owns the document: it parses the XAML, it is the only thing that
/// edits the tree, and it builds the live objects a designer shows.
/// </item>
/// <item>
/// <b>ArxisStudio.DesignEditor</b> owns the surface: viewport, grid, selection, handles and
/// gestures. It reads the control tree and never writes to it — structural intent arrives as a
/// request this application answers by editing the document.
/// </item>
/// </list>
/// <para>
/// So every user gesture takes the same route: the editor reports it, this class turns it into a
/// document edit, and the live tree is rebuilt from the document. The document is the truth and the
/// canvas is a view of it — never the other way round, because a canvas that could disagree with
/// the file is a designer that loses work.
/// </para>
/// </remarks>
public sealed partial class DesignerViewModel : Observable, IDisposable
{
    private readonly ProjectWorkspace _workspace = new(new MSBuildProjectProvider());
    private readonly CancellationTokenSource _shutdown = new();

    private bool _disposed;

    public DesignerViewModel()
    {
        OpenCommand = new RelayCommand(() => Run(OpenAsync));
        SaveCommand = new RelayCommand(() => Run(SaveAsync), () => CanSave);
        NewFormCommand = new RelayCommand(() => Run(NewFormAsync), () => IsLoaded);

        RestoreCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Restore)), CanOperate);
        BuildCommand = new RelayCommand(() => Run(() => ExecuteAsync(ProjectOperationKind.Build)), CanOperate);

        InitialiseShell();
        InitialiseRun();
        InitialisePackages();
        InitialiseToolbox();

        _workspace.SnapshotChanged += OnSnapshotChanged;

        Log("Ready. Open a .sln, .slnx or .csproj, then open a form from the Project panel.");
        Log(Environment());
    }

    /// <summary>The forms open on the canvas. The editor's items are forms, not controls.</summary>
    /// <remarks>
    /// An infinite surface holding several forms at once is the arrangement this buys, and it is why
    /// the editor is bound to a collection rather than to one root: comparing two dialogs side by
    /// side is a thing designers do, and nothing here had to be added for it.
    /// </remarks>
    public ObservableCollection<FormViewModel> Forms { get; } = [];

    public ObservableCollection<FormViewModel> SelectedForms { get; } = [];

    public ObservableCollection<string> Output { get; } = [];

    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];

    public RelayCommand OpenCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand NewFormCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand BuildCommand { get; }

    /// <summary>Set by the view, because picking a file needs a window to hang a dialog off.</summary>
    public Func<Task<string?>>? PickEntryPoint { get; set; }

    /// <summary>Set by the view, for the same reason.</summary>
    public Func<string, Task<string?>>? AskForName { get; set; }

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
    } = "No project";

    public string Progress
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    public double Zoom
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(ZoomText));
                Raise(nameof(CanvasCaption));
                Raise(nameof(CanvasSize));
                Raise(nameof(CanvasSizeAndZoom));
            }
        }
    } = 1.0;

    /// <summary>The zoom as a person reads it.</summary>
    public string ZoomText => $"{Zoom * 100:F0}%";

    /// <summary>
    /// Whether a drag snaps to the grid, and whether it aligns to its neighbours.
    /// </summary>
    /// <remarks>
    /// Both on, and both switchable, because either one is occasionally the wrong help. Holding Alt
    /// bypasses them for one gesture — which is the setting somebody actually reaches for, and the
    /// reason these two are a pair of checkboxes rather than a settings page.
    /// </remarks>
    public bool SnapToGrid
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool SnapToGuides
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// Opens an entry point, and optionally a form in it, from the command line.
    /// </summary>
    /// <remarks>
    /// So the designer can be pointed at a project without a mouse, which is what makes it usable as
    /// a smoke test: the output pane is mirrored to standard output, so a run says whether the
    /// document parsed, how many elements it mapped, and what went wrong when something did.
    /// </remarks>
    /// <param name="path">The solution or project to open.</param>
    /// <param name="form">A form to open once it has loaded, matched by file name.</param>
    public void OpenAtStartup(string path, string? form = null) => Run(async () =>
    {
        EntryPoint = CanonicalPath.Create(path);

        await LoadAsync();

        if (form is not { Length: > 0 })
        {
            return;
        }

        // The project panel is filled from the snapshot notification, which is posted rather than
        // raised inline. Yielding to a lower priority lets that arrive first, so there is something
        // to look the name up in.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        if (ProjectForms.FirstOrDefault(candidate =>
            candidate.Name.Equals(form, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            await OpenFormAsync(found);
        }
        else
        {
            Log($"! no form called {form} in this project");
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();

        StopProject();
        CloseAllForms();
        ReleaseFeed();

        _ = DisposeWorkspaceAsync();

        _shutdown.Dispose();
        DisposeHttp();
    }

    private CanonicalPath EntryPoint { get; set; }

    private bool CanOperate() => IsLoaded && !IsBusy;

    private static string Environment()
    {
        try
        {
            return $"MSBuild: {MSBuildEnvironment.Register()}";
        }
        catch (InvalidOperationException exception)
        {
            return $"! No MSBuild: {exception.Message} Opening a project will report "
                + $"{MSBuildDiagnosticCodes.MSBuildNotFound}.";
        }
    }

    /// <summary>Runs work the user asked for, and keeps the window honest about it.</summary>
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
    /// Runs work that follows from something rather than from a click, without the busy flag.
    /// </summary>
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

        EntryPoint = CanonicalPath.Create(picked);

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Log($"Opening {EntryPoint.FileName}…");

        WorkspaceLoadResult result = await _workspace.LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = _workspace.Identity,
                EntryPointPath = EntryPoint,

                // Items are how the Project panel finds the forms, so the designer asks for them.
                Options = new WorkspaceLoadOptions { IncludeItems = true },
            },
            _shutdown.Token);

        Log($"  {result.Status} — {result.Diagnostics.Length} diagnostic(s)");

        IsLoaded = result.Snapshot is not null;

        Raise(nameof(StatusLeft));
        Raise(nameof(ProjectName));
        Raise(nameof(EntryPointName));
        Raise(nameof(ConfigurationName));
        ShowDiagnostics(result.Diagnostics);
        RefreshAllCommands();
    }

    private void OnSnapshotChanged(object? sender, WorkspaceChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Show(e.Snapshot));

    private void Show(SolutionSnapshot snapshot)
    {
        Status = $"{snapshot.Name} — v{snapshot.Version}"
            + (snapshot.HasErrors ? ", with errors" : string.Empty);

        BuildProjectTree(snapshot);
        Raise(nameof(StatusLeft));
        RefreshAllCommands();
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

    private void ShowDiagnostics(System.Collections.Generic.IEnumerable<ProjectDiagnostic> diagnostics)
    {
        Diagnostics.Clear();

        foreach (ProjectDiagnostic diagnostic in diagnostics)
        {
            Diagnostics.Add(DiagnosticRow.From(diagnostic));
        }

        Raise(nameof(ProblemCount));
    }

    private void RefreshAllCommands()
    {
        OpenCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        NewFormCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        RunCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshPackageCommands();
    }

    private void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(message));

            return;
        }

        Output.Add(message);
        Record(message);
        Console.WriteLine(message);

        while (Output.Count > 500)
        {
            Output.RemoveAt(0);
        }
    }

    private static string Describe(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}

/// <summary>A diagnostic flattened for a list.</summary>
public sealed record DiagnosticRow(string Severity, string Code, string Message, string Where)
{
    public static DiagnosticRow From(ProjectDiagnostic diagnostic) => new(
        diagnostic.Severity.ToString(),
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.FilePath.IsEmpty ? string.Empty : diagnostic.FilePath.FileName);
}
