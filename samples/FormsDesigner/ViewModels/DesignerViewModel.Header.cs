using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using Avalonia.Threading;

namespace FormsDesigner.ViewModels;

/// <summary>
/// The header: the branch, the run target, and what settings hides behind one icon.
/// </summary>
/// <remarks>
/// The design's header carries four pieces of state the rest of the window does not — a branch, a
/// run target, a running clock, and a settings menu — and each of them is read rather than assumed.
/// The branch in particular: a designer that printed <c>main</c> for every project would be wrong
/// for anyone who had checked something else out, and wrong invisibly.
/// </remarks>
public sealed partial class DesignerViewModel
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _startedAt;
    private int _runTarget;

    /// <summary>The branch checked out where the project lives, empty when there is no repository.</summary>
    public string BranchName
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(HasBranch));
            }
        }
    } = string.Empty;

    /// <summary>How far ahead of its upstream that branch is, as the design's small badge.</summary>
    public string BranchAhead { get; private set => Set(ref field, value); } = string.Empty;

    /// <summary>Whether there is a branch to show at all.</summary>
    public bool HasBranch => BranchName.Length > 0;

    /// <summary>What Run would start, which is a project rather than a form.</summary>
    public string RunTargetName => Runnable() is { Length: > 0 } projects
        ? projects[Math.Min(_runTarget, projects.Length - 1)].Name
        : "—";

    /// <summary>The running badge, counting from the moment the process started.</summary>
    public string RunningText { get; private set => Set(ref field, value); } = "Running";

    /// <summary>What the settings menu offers, which is whichever variant is not on.</summary>
    public string ThemeMenuHeader => IsDark ? "Switch to light" : "Switch to dark";

    /// <summary>Puts the caret in the toolbox filter, which is what the design's magnifier does.</summary>
    public RelayCommand FocusSearchCommand { get; private set; } = null!;

    /// <summary>Moves to the next runnable project in the solution.</summary>
    public RelayCommand PickRunTargetCommand { get; private set; } = null!;

    public RelayCommand ToggleSnapToGridCommand { get; private set; } = null!;

    public RelayCommand ToggleSnapToGuidesCommand { get; private set; } = null!;

    /// <summary>Raised when the magnifier is clicked, so the view can move focus.</summary>
    public event EventHandler? SearchRequested;

    private void InitialiseHeader()
    {
        FocusSearchCommand = new RelayCommand(() => SearchRequested?.Invoke(this, EventArgs.Empty));

        PickRunTargetCommand = new RelayCommand(() =>
        {
            ProjectSnapshot[] projects = Runnable();

            if (projects.Length > 1)
            {
                _runTarget = (_runTarget + 1) % projects.Length;

                Raise(nameof(RunTargetName));
            }
        });

        ToggleSnapToGridCommand = new RelayCommand(() => SnapToGrid = !SnapToGrid);
        ToggleSnapToGuidesCommand = new RelayCommand(() => SnapToGuides = !SnapToGuides);

        _clock.Tick += (_, _) =>
            RunningText = $"Running · {DateTime.UtcNow - _startedAt:mm\\:ss}";
    }

    /// <summary>Starts the clock the running badge counts on.</summary>
    private void StartClock()
    {
        _startedAt = DateTime.UtcNow;
        RunningText = "Running · 00:00";

        _clock.Start();
    }

    private void StopClock() => _clock.Stop();

    /// <summary>Freezes the badge while the application is held, because nothing is running to time.</summary>
    private void PauseClock()
    {
        _clock.Stop();

        RunningText = $"Paused · {DateTime.UtcNow - _startedAt:mm\\:ss}";
    }

    /// <summary>And starts it again from where the application actually started.</summary>
    /// <remarks>
    /// The text is written here as well as by the tick, because the first tick is a second away and
    /// a badge that still says Paused is the one thing a person checks after pressing resume.
    /// </remarks>
    private void ResumeClock()
    {
        RunningText = $"Running · {DateTime.UtcNow - _startedAt:mm\\:ss}";

        _clock.Start();
    }

    /// <summary>
    /// The solution's runnable projects, in the order the snapshot has them.
    /// </summary>
    /// <remarks>
    /// Runnable is the same test the run path applies — a project that produced a runtime
    /// configuration is one the runtime can start, and a library is not — rather than a second
    /// definition that could disagree with it.
    /// </remarks>
    internal ProjectSnapshot[] Runnable() =>
        _workspace.CurrentSnapshot is { } snapshot
            ? snapshot.Projects
                .Where(static p => p.Outputs.Any(static o => o.Kind == OutputArtifactKind.RuntimeConfiguration))
                .ToArray()
            : [];

    /// <summary>The runnable project the header names, when there is one.</summary>
    internal ProjectSnapshot? SelectedRunTarget() =>
        Runnable() is { Length: > 0 } projects ? projects[Math.Min(_runTarget, projects.Length - 1)] : null;

    /// <summary>
    /// Reads the branch out of the repository the project sits in.
    /// </summary>
    /// <remarks>
    /// Git is asked rather than <c>.git</c> parsed by hand: the head can be a symbolic ref, a
    /// detached hash, or a worktree pointing somewhere else entirely, and <c>git</c> already knows
    /// the difference. A project with no repository, or a machine with no git, simply has no branch
    /// to show, and the header leaves the space empty instead of inventing one.
    /// </remarks>
    private async Task ReadBranchAsync(CancellationToken cancellationToken)
    {
        BranchName = string.Empty;
        BranchAhead = string.Empty;

        if (EntryPoint.IsEmpty)
        {
            return;
        }

        string directory = EntryPoint.Directory.Value;
        string? branch = await GitAsync(directory, "rev-parse --abbrev-ref HEAD", cancellationToken)
            .ConfigureAwait(true);

        if (branch is not { Length: > 0 } || branch == "HEAD")
        {
            return;
        }

        BranchName = branch;

        string? ahead = await GitAsync(directory, "rev-list --count @{u}..HEAD", cancellationToken)
            .ConfigureAwait(true);

        BranchAhead = ahead is { Length: > 0 } && ahead != "0" ? "↑" + ahead : string.Empty;
    }

    /// <summary>One git command, or <see langword="null"/> when it could not be asked.</summary>
    private static async Task<string?> GitAsync(
        string directory,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(true);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No git, no repository, no branch. The header shows nothing and the designer carries on.
            return null;
        }
    }
}
