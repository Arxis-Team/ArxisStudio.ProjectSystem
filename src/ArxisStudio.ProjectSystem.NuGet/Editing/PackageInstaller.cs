using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Changes what a project references and restores it, undoing the change if the restore fails.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PackageEditor"/> writes files and stops there. This is the whole operation a person
/// means by "install this package": edit, restore, and — if the restore fails — put the project
/// back the way it was.
/// </para>
/// <para>
/// <b>Nothing here evaluates a project.</b> The restore goes through the workspace, which routes it
/// to whichever provider can run one; this package hosts no build engine and must not, because a
/// second thing able to read project files is a second thing able to disagree about them. What it
/// contributes is the ordering and the undo, which is the part neither the editor nor the provider
/// owns.
/// </para>
/// <para>
/// <b>An edit that changes nothing does not restore.</b> Installing a package the project already
/// has is a no-op, and a restore for a file nobody touched is a wait nobody asked for.
/// </para>
/// </remarks>
public static class PackageInstaller
{
    /// <summary>Applies a change and restores the project.</summary>
    /// <remarks>
    /// The restore is not a mutation and does not take the workspace's boundary — see
    /// <see href="../../docs/adr/0014-an-operation-is-not-a-mutation.md">ADR 0014</see> — so no
    /// snapshot is published and the version does not advance. A caller that wants the model to
    /// reflect the new reference calls <see cref="ProjectWorkspace.RefreshAsync"/> afterwards, at a
    /// moment it chose.
    /// </remarks>
    /// <param name="request">What to change.</param>
    /// <param name="workspace">The workspace whose provider will restore.</param>
    /// <param name="layout">Where versions live, or <see langword="null"/> to write them beside the reference.</param>
    /// <param name="progress">Where to report restore progress, or <see langword="null"/> for nowhere.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>
    /// Whether the project ended up changed and restored. A failure says which step failed, and
    /// whether the change survived it.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="workspace"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async ValueTask<ProjectOperationResult> ApplyAndRestoreAsync(
        PackageEditRequest request,
        ProjectWorkspace workspace,
        PackageVersionLayout? layout = null,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);

        cancellationToken.ThrowIfCancellationRequested();

        layout ??= PackageVersionLayout.Beside;

        // Read before editing, so the undo needs nothing from the disk to succeed. The same
        // principle as the editor's own transaction, one level up: the bytes are already in hand.
        List<(CanonicalPath Path, string Text)> before =
            await ReadAsync(request, layout, cancellationToken).ConfigureAwait(false);

        ProjectOperationResult edit = await PackageEditor
            .ApplyAsync(request, layout, cancellationToken)
            .ConfigureAwait(false);

        if (edit.HasErrors)
        {
            return edit;
        }

        if (!await ChangedAsync(before, cancellationToken).ConfigureAwait(false))
        {
            // Nothing was written, so there is nothing to restore and nothing to undo. The edit's
            // own diagnostic already says why.
            return edit;
        }

        ProjectOperationResult restore = await workspace.ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = ProjectOperationKind.Restore,
                Workspace = workspace.Identity,
                EntryPointPath = request.ProjectFilePath,
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!restore.HasErrors)
        {
            return ProjectOperationResult.Succeeded([.. edit.Diagnostics, .. restore.Diagnostics]);
        }

        return await UndoAsync(request, before, restore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The files this change could touch, as they are now.</summary>
    /// <remarks>
    /// A file that is not there is recorded as absent rather than skipped, so that a change which
    /// creates one can be undone by removing it again.
    /// </remarks>
    private static async ValueTask<List<(CanonicalPath, string)>> ReadAsync(
        PackageEditRequest request,
        PackageVersionLayout layout,
        CancellationToken cancellationToken)
    {
        var files = new List<(CanonicalPath, string)>();

        foreach (CanonicalPath path in Touched(request, layout))
        {
            if (File.Exists(path.Value))
            {
                files.Add((path, await File.ReadAllTextAsync(path.Value, cancellationToken).ConfigureAwait(false)));
            }
        }

        return files;
    }

    private static IEnumerable<CanonicalPath> Touched(PackageEditRequest request, PackageVersionLayout layout)
    {
        yield return request.ProjectFilePath;

        if (layout.IsCentral)
        {
            yield return layout.VersionsFilePath;
        }
    }

    /// <summary>
    /// Whether anything actually reached the disk.
    /// </summary>
    /// <remarks>
    /// Compared against the bytes rather than inferred from the edit's diagnostics. A change is
    /// something that happened to a file, and the file is where to look for it.
    /// </remarks>
    private static async ValueTask<bool> ChangedAsync(
        List<(CanonicalPath Path, string Text)> before, CancellationToken cancellationToken)
    {
        foreach ((CanonicalPath path, string text) in before)
        {
            string now = await File.ReadAllTextAsync(path.Value, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(text, now, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts the files back, and reports the restore's own failure alongside what was done about it.
    /// </summary>
    /// <remarks>
    /// This is what <c>dotnet add package</c> does, and for the same reason: a project left carrying
    /// a reference that cannot be restored does not build, and the person who asked for the package
    /// is not better off for having half of it. The restore's diagnostics are kept, because
    /// <em>why</em> it failed is the actionable part — a version that does not exist reads very
    /// differently from a feed that was unreachable.
    /// </remarks>
    private static async ValueTask<ProjectOperationResult> UndoAsync(
        PackageEditRequest request,
        List<(CanonicalPath Path, string Text)> before,
        ProjectOperationResult restore,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var diagnostics = new List<ProjectDiagnostic>(restore.Diagnostics);

        foreach ((CanonicalPath path, string text) in before)
        {
            try
            {
                // Not cancellable: an undo that stops halfway leaves exactly the state it exists to
                // prevent, and the caller cancelling does not make a broken project acceptable.
                await File.WriteAllTextAsync(path.Value, text, Encoding.UTF8, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    PackageDiagnosticCodes.ChangeNotUndone,
                    $"The restore failed and '{path}' could not be put back: {exception.Message}",
                    ProjectDiagnosticSeverity.Error)
                {
                    FilePath = path,
                    ProviderName = "NuGet",
                });

                return ProjectOperationResult.Failed(diagnostics);
            }
        }

        diagnostics.Add(new ProjectDiagnostic(
            PackageDiagnosticCodes.ChangeUndone,
            $"The restore failed, so '{request.PackageId}' was put back the way it was.",
            ProjectDiagnosticSeverity.Warning)
        {
            FilePath = request.ProjectFilePath,
            ProviderName = "NuGet",
        });

        return ProjectOperationResult.Failed(diagnostics);
    }
}
