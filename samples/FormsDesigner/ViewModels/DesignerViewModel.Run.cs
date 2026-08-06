using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using Avalonia.Threading;

namespace FormsDesigner.ViewModels;

public sealed partial class DesignerViewModel
{
    private Process? _running;

    public RelayCommand RunCommand { get; private set; } = null!;

    public RelayCommand StopCommand { get; private set; } = null!;

    public bool IsRunning
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

    private void InitialiseRun()
    {
        RunCommand = new RelayCommand(() => Run(RunProjectAsync), () => CanOperate() && !IsRunning);
        StopCommand = new RelayCommand(StopProject, () => IsRunning);
    }

    /// <summary>
    /// Builds the project and starts what it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which file to start comes from <see cref="ProjectSnapshot.Outputs"/> rather than from a
    /// listing of <c>bin</c>: the assembly artifact is what the project says it produces, and the
    /// runtime configuration is how it says it is startable at all. A project with neither is a
    /// library, and saying so is better than starting something arbitrary.
    /// </para>
    /// <para>
    /// A build that failed stops it. Running the previous build after a failed one is how somebody
    /// spends an afternoon debugging code they did not write.
    /// </para>
    /// </remarks>
    private async Task RunProjectAsync()
    {
        if (Project() is not { } project)
        {
            return;
        }

        Log($"Building {project.Name} before running…");

        if (await ExecuteAsync(ProjectOperationKind.Build) != ProjectOperationStatus.Succeeded)
        {
            Log("  the build failed — not starting anything");

            return;
        }

        if (project.Outputs.FirstOrDefault(o => o.Kind == OutputArtifactKind.Assembly) is not { } assembly)
        {
            Log($"  {project.Name} produces no assembly to start");

            return;
        }

        if (!project.Outputs.Any(o => o.Kind == OutputArtifactKind.RuntimeConfiguration))
        {
            Log($"  {project.Name} is a library — nothing to start");

            return;
        }

        Start(assembly.Path, project.Name);
    }

    private void Start(CanonicalPath assembly, string name)
    {
        if (!File.Exists(assembly.Value))
        {
            Log($"  {assembly.FileName} is not there");

            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{assembly.Value}\"")
            {
                WorkingDirectory = assembly.Directory.Value,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) => Mirror(e.Data);
        process.ErrorDataReceived += (_, e) => Mirror(e.Data);

        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Log($"  {name} exited with code {process.ExitCode}");

            IsRunning = false;
            _running = null;

            process.Dispose();
        });

        Log($"Running {assembly.FileName}…");

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _running = process;
        IsRunning = true;
    }

    private void Mirror(string? line)
    {
        if (line is { Length: > 0 })
        {
            Log($"  │ {line}");
        }
    }

    private void StopProject()
    {
        if (_running is not { HasExited: false } process)
        {
            return;
        }

        Log("Stopping…");

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Log($"  already gone ({exception.GetType().Name})");
        }
    }

    /// <summary>Restores or builds through the workspace, which routes it to the provider.</summary>
    /// <remarks>
    /// An operation is not a mutation: it changes what is on disk rather than what the workspace
    /// knows, so nothing is published and the version does not advance. A restore is the exception
    /// worth refreshing after, because it rewrites an evaluation input.
    /// </remarks>
    private Task<ProjectOperationStatus> ExecuteAsync(ProjectOperationKind kind) =>
        Project() is { } project
            ? ExecuteAsync(kind, project)
            : Task.FromResult(ProjectOperationStatus.Failed);

    private async Task<ProjectOperationStatus> ExecuteAsync(
        ProjectOperationKind kind, ProjectSnapshot project)
    {

        Log($"{kind} {project.Name}…");

        var progress = new Progress<ProjectOperationProgress>(report => Progress = report.Message);

        ProjectOperationResult result = await _workspace.ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = kind,
                Workspace = _workspace.Identity,
                EntryPointPath = EntryPoint,

                // The one project rather than the whole solution: a designer builds what it is
                // showing, and waiting for everything else is a wait nobody asked for.
                Projects = [project.Identity],
            },
            progress,
            _shutdown.Token);

        Log($"  {result.Status} — {Describe(result.Diagnostics.Length, "diagnostic")}");

        ShowDiagnostics(result.Diagnostics);

        if (kind == ProjectOperationKind.Restore && result.Status == ProjectOperationStatus.Succeeded)
        {
            await _workspace.RefreshAsync(_shutdown.Token);
        }

        return result.Status;
    }

    /// <summary>The project everything acts on: whichever one owns the form being designed.</summary>
    private ProjectSnapshot? Project()
    {
        if (_workspace.CurrentSnapshot is not { } snapshot)
        {
            return null;
        }

        if (ActiveForm is { } form && snapshot.TryGetProjectForFile(form.File, out ProjectSnapshot? owner))
        {
            return owner;
        }

        return snapshot.Projects.FirstOrDefault();
    }
}
