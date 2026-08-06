using System;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using Avalonia.Threading;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
{
    /// <summary>
    /// Restores, builds, rebuilds or cleans the open entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation does not take the workspace's mutation boundary and publishes nothing — it
    /// changed what is on disk, not what the model knows (ADR 0014). So the window explicitly
    /// refreshes afterwards when the caller asked for something that changes what a project says,
    /// which is exactly the trade that ADR describes: the version means one thing, and catching up
    /// is a decision rather than a side effect.
    /// </para>
    /// <para>
    /// Progress arrives on an engine thread and every report has to cross to the UI one.
    /// </para>
    /// </remarks>
    private async Task ExecuteAsync(ProjectOperationKind kind)
    {
        Log($"{kind}…");

        var progress = new Progress<ProjectOperationProgress>(
            report => Dispatcher.UIThread.Post(() => Progress = report.Message));

        ProjectOperationResult result = await _workspace.ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = kind,
                Workspace = _workspace.Identity,
                EntryPointPath = _entryPoint,
            },
            progress,
            _shutdown.Token);

        Log($"  {kind}: {result.Status} — {Describe(result.Diagnostics.Length, "diagnostic")}");

        ShowDiagnostics(result.Diagnostics);

        // A restore rewrites project.assets.json, which is an evaluation input of every project that
        // has packages. The model is now behind the disk, and nothing but a refresh fixes that.
        if (kind == ProjectOperationKind.Restore && result.Status == ProjectOperationStatus.Succeeded)
        {
            Log("  restore changed what projects resolve; refreshing the model");

            await RefreshAsync();
        }
    }
}
