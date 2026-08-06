using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using Avalonia.Threading;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
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
        RunCommand = new RelayCommand(() => Run(RunProjectAsync), CanRun);
        StopCommand = new RelayCommand(StopProject, () => IsRunning);
    }

    /// <summary>
    /// Whether the selected project is something that can be started.
    /// </summary>
    /// <remarks>
    /// Decided from the model rather than by looking at the disk: a project says whether it produces
    /// an executable, and <see cref="OutputArtifactKind.RuntimeConfiguration"/> is only present when
    /// the build emits one — which for a .NET application is exactly the file that lets it start.
    /// </remarks>
    private bool CanRun() =>
        !IsBusy && !IsRunning && SelectedProject is { } project && IsExecutable(project);

    private static bool IsExecutable(ProjectSnapshot project) =>
        project.Outputs.Any(static output => output.Kind == OutputArtifactKind.RuntimeConfiguration)
            || string.Equals(project.Kind, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.Kind, "WinExe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the selected project and starts what it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the output descriptors are for. The model says which file is the assembly, and
    /// the responsibilities document is explicit that a consumer must never "return an arbitrary
    /// <c>bin</c> DLL when several outputs exist" — so nothing here globs a directory. The
    /// <c>.runtimeconfig.json</c> beside it is what <c>dotnet</c> needs to host it, and it is in the
    /// model too.
    /// </para>
    /// <para>
    /// The build goes through the workspace so a failure is reported the same way any other is, and
    /// nothing is started if it failed: running the previous build after a failed one is how a
    /// person ends up debugging code they did not write.
    /// </para>
    /// </remarks>
    private async Task RunProjectAsync()
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        if (Assembly(project) is not { } assembly)
        {
            Log($"! {project.Name} declares no assembly output to run.");

            return;
        }

        Log($"Building {project.Name} before running…");

        var progress = new Progress<ProjectOperationProgress>(
            report => Dispatcher.UIThread.Post(() => Progress = report.Message));

        ProjectOperationResult built = await _workspace.ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = ProjectOperationKind.Build,
                Workspace = _workspace.Identity,
                EntryPointPath = project.ProjectFilePath,
            },
            progress,
            _shutdown.Token);

        ShowDiagnostics(built.Diagnostics);

        if (built.Status != ProjectOperationStatus.Succeeded)
        {
            Log("  build failed; not running");

            // The engine's own codes, which are the actionable part: NETSDK1004 for a project that
            // was never restored reads very differently from CS0103.
            foreach (ProjectDiagnostic diagnostic in built.Diagnostics.Where(static d => d.IsError).Take(5))
            {
                Log($"  ! {diagnostic.Code}: {diagnostic.Message}");
            }

            return;
        }

        if (!File.Exists(assembly.Value))
        {
            // The model says where a build puts things, not that they are there. Usually the build
            // above has just made it true; when it has not, saying so beats a launch failure.
            Log($"! {assembly.FileName} is not there, though the build reported success.");

            return;
        }

        Start(project, assembly);
    }

    /// <summary>The one output that can be started, chosen by kind rather than by extension.</summary>
    private static CanonicalPath? Assembly(ProjectSnapshot project)
    {
        foreach (OutputArtifact output in project.Outputs)
        {
            if (output.Kind == OutputArtifactKind.Assembly)
            {
                return output.Path;
            }
        }

        return null;
    }

    private void Start(ProjectSnapshot project, CanonicalPath assembly)
    {
        // A framework-dependent .NET application is a library plus a runtimeconfig, and `dotnet`
        // is what hosts it. The apphost beside it would work too, and does not exist on every
        // platform or for every project; the model can tell us the runtimeconfig does.
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = assembly.Directory.Value,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add(assembly.Value);

        Log($"Running {assembly.FileName}…");

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => Mirror(e.Data);
        process.ErrorDataReceived += (_, e) => Mirror(e.Data);
        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Log($"  {project.Name} exited with code {process.ExitCode}");

            IsRunning = false;
            _running = null;

            process.Dispose();
        });

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

    /// <summary>
    /// Stops what was started.
    /// </summary>
    /// <remarks>
    /// The whole tree, because a launched application is entitled to have started children of its
    /// own and killing only the parent leaves them holding the files the next build needs.
    /// </remarks>
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
            // It exited between the check and the kill, which is the ordinary race and not a
            // failure: the thing being asked for has happened. Those are the two Kill documents --
            // SystemException would have been almost every exception there is, swallowing a genuine
            // fault as "already gone".
            Log($"  already gone ({exception.GetType().Name})");
        }
    }
}
