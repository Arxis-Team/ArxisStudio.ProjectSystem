using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FormsDesigner.ViewModels;

/// <summary>What a new project is, once somebody has answered for it.</summary>
public sealed record NewProjectRequest(string Name, string Location, ProjectTemplate Template);

/// <summary>
/// The screen the studio opens on: what you have been working on, and the ways to start.
/// </summary>
/// <remarks>
/// <para>
/// A designer that opens straight into an empty canvas has asked its first question — which project?
/// — by not asking it. This is the answer every studio gives: the projects you had open, the
/// templates you can start from, and the two ways in from somewhere else.
/// </para>
/// <para>
/// It owns no workspace and loads nothing. Choosing a project raises <see cref="ProjectChosen"/> and
/// the shell takes it from there, which keeps the one expensive thing this application does — MSBuild
/// evaluation — out of a window whose job is to be up instantly.
/// </para>
/// </remarks>
public sealed class WelcomeViewModel : Observable
{
    public WelcomeViewModel()
    {
        NewProjectCommand = new RelayCommand(() => Detached(() => CreateAsync(Templates[0])));
        OpenCommand = new RelayCommand(() => Detached(OpenAsync));
        CloneCommand = new RelayCommand(() => Detached(CloneAsync));

        ShowRecent(RecentProjects.Read());
    }

    /// <summary>The projects offered for reopening, filtered by whatever is in the search box.</summary>
    public ObservableCollection<RecentProject> Recent { get; } = [];

    public IReadOnlyList<ProjectTemplate> Templates { get; } = ProjectScaffold.Templates;

    public RelayCommand NewProjectCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloneCommand { get; }

    /// <summary>Raised with the project file to open, once one has been chosen or made.</summary>
    public event EventHandler<string>? ProjectChosen;

    /// <summary>Asked of the view: a project file to open.</summary>
    public Func<Task<string?>>? PickProjectFile { get; set; }

    /// <summary>Asked of the view: a folder, for the two paths that need one.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Asked of the view: the name, place and shape of a new project.</summary>
    public Func<ProjectTemplate, Task<NewProjectRequest?>>? AskForNewProject { get; set; }

    /// <summary>Asked of the view: one line of text, for the repository to clone.</summary>
    public Func<string, string, Task<string?>>? AskForText { get; set; }

    /// <summary>
    /// Which of the rail's sections the page is showing.
    /// </summary>
    /// <remarks>
    /// Three, because three is what this window has to say. A rail item for something that opens an
    /// empty page is worse than a rail without it.
    /// </remarks>
    public string Section
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(IsProjects));
                Raise(nameof(IsTemplates));
                Raise(nameof(IsLearn));
                Raise(nameof(ShowTemplates));
            }
        }
    } = "projects";

    public bool IsProjects => Section == "projects";

    public bool IsTemplates => Section == "templates";

    public bool IsLearn => Section == "learn";

    /// <summary>The cards are the point of two of the three sections.</summary>
    public bool ShowTemplates => Section is "projects" or "templates";

    /// <summary>What the studio is, under its name.</summary>
    public string Edition { get; } = $"2026.2 · Avalonia {ProjectScaffold.AvaloniaVersion}";

    public string Search
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                ShowRecent(RecentProjects.Read());
            }
        }
    } = string.Empty;

    /// <summary>Whether there is anything to reopen, which decides what the middle of the page is.</summary>
    public bool HasRecent => Recent.Count > 0;

    /// <summary>What went wrong with the last thing that was asked for, when something did.</summary>
    public string Problem
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(HasProblem));
            }
        }
    } = string.Empty;

    public bool HasProblem => Problem.Length > 0;

    /// <summary>Whether something is being made right now, which the page says rather than freezing.</summary>
    public bool IsBusy
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>Opens a project from the recent list. Called by the view.</summary>
    public void Open(RecentProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.Exists)
        {
            Problem = $"{project.Name} больше нет по этому пути";

            ShowRecent(RecentProjects.Forget(project.Path));

            return;
        }

        Choose(project.Path);
    }

    /// <summary>Takes a project out of the list without touching what is on disk. Called by the view.</summary>
    public void Forget(RecentProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        ShowRecent(RecentProjects.Forget(project.Path));
    }

    /// <summary>Starts a new project from one of the cards. Called by the view.</summary>
    public void Start(ProjectTemplate template) => Detached(() => CreateAsync(template));

    private async Task CreateAsync(ProjectTemplate template)
    {
        if (AskForNewProject is null || await AskForNewProject(template) is not { } request)
        {
            return;
        }

        IsBusy = true;
        Problem = string.Empty;

        try
        {
            string project = await ProjectScaffold.CreateAsync(
                request.Template,
                request.Location,
                request.Name,
                System.Threading.CancellationToken.None);

            Choose(project);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenAsync()
    {
        if (PickProjectFile is null || await PickProjectFile() is not { Length: > 0 } picked)
        {
            return;
        }

        Choose(picked);
    }

    /// <summary>
    /// Clones a repository and opens what it turns out to hold.
    /// </summary>
    /// <remarks>
    /// git is run rather than reimplemented, and its output is kept: a clone that fails says why —
    /// authentication, a name that does not resolve, a directory in the way — and passing that
    /// sentence on is more useful than any message this could write instead.
    /// </remarks>
    private async Task CloneAsync()
    {
        if (AskForText is null || PickFolder is null)
        {
            return;
        }

        if (await AskForText("Клонировать репозиторий", "https://github.com/…") is not { Length: > 0 } url)
        {
            return;
        }

        if (await PickFolder("Куда клонировать") is not { Length: > 0 } parent)
        {
            return;
        }

        IsBusy = true;
        Problem = string.Empty;

        try
        {
            string folder = Path.Combine(parent, NameFromUrl(url));

            (int code, string error) = await GitCloneAsync(url, folder);

            if (code != 0)
            {
                Problem = error is { Length: > 0 } ? error : $"git clone вернул {code}";

                return;
            }

            if (FindProject(folder) is not { } project)
            {
                Problem = "В репозитории нет .sln, .slnx или .csproj";

                return;
            }

            Choose(project);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            Problem = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<(int Code, string Error)> GitCloneAsync(string url, string folder)
    {
        using var git = new Process
        {
            StartInfo = new ProcessStartInfo("git", $"clone \"{url}\" \"{folder}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        git.Start();

        string error = await git.StandardError.ReadToEndAsync().ConfigureAwait(true);

        await git.WaitForExitAsync().ConfigureAwait(true);

        return (git.ExitCode, error.Trim());
    }

    private static string NameFromUrl(string url)
    {
        string trimmed = url.TrimEnd('/');

        int slash = trimmed.LastIndexOf('/');

        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The entry point a cloned repository offers: a solution first, a project otherwise.</summary>
    private static string? FindProject(string folder)
    {
        foreach (string pattern in new[] { "*.sln", "*.slnx", "*.csproj" })
        {
            string? found = Directory
                .EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
                .OrderBy(static path => path.Length)
                .FirstOrDefault();

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void Choose(string path)
    {
        ShowRecent(RecentProjects.Remember(path, Path.GetFileNameWithoutExtension(path)));

        ProjectChosen?.Invoke(this, path);
    }

    private void ShowRecent(IReadOnlyList<RecentProject> projects)
    {
        Recent.Clear();

        foreach (RecentProject project in projects)
        {
            if (Search is { Length: > 0 } filter
                && !project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !project.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Recent.Add(project);
        }

        Raise(nameof(HasRecent));
    }

    /// <summary>
    /// Runs work that nothing awaits, which is every command on this page.
    /// </summary>
    /// <remarks>
    /// A dialog is awaited inside, so the command cannot be synchronous; and nothing can await the
    /// command, so the failure has to be caught here or it is lost. The page reports it in the one
    /// place a person is already looking.
    /// </remarks>
    private async void Detached(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception error)
        {
            Problem = $"{error.GetType().Name}: {error.Message}";
        }
    }
}
