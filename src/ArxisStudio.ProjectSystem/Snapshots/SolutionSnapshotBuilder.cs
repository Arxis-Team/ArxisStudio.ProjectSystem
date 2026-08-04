using System;
using System.Collections.Generic;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Assembles a <see cref="SolutionSnapshot"/>. A provider fills one of these in and calls
/// <see cref="ToSnapshot"/>.
/// </summary>
/// <remarks>
/// <b>Not thread-safe</b>, on the same terms as <see cref="ProjectSnapshotBuilder"/>, and for the
/// same reason: it exists so that copying happens once, at the boundary.
/// </remarks>
public sealed class SolutionSnapshotBuilder
{
    /// <summary>Gets or sets the workspace this snapshot belongs to.</summary>
    public WorkspaceIdentity Workspace { get; set; }

    /// <summary>
    /// Gets or sets the solution's identity. Leave as <see cref="SolutionIdentity.None"/> when the
    /// entry point is a standalone project.
    /// </summary>
    public SolutionIdentity Solution { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the name of the provider producing this snapshot.</summary>
    public string? ProviderName { get; set; }

    /// <summary>Gets or sets the request this snapshot answers.</summary>
    public WorkspaceLoadRequest? Request { get; set; }

    /// <summary>Gets the projects.</summary>
    public IList<ProjectSnapshot> Projects { get; } = [];

    /// <summary>Gets the diagnostics about the solution as a whole.</summary>
    public IList<ProjectDiagnostic> Diagnostics { get; } = [];

    /// <summary>Materialises an immutable snapshot from the current contents.</summary>
    /// <returns>
    /// The snapshot, at <see cref="WorkspaceVersion.None"/>. A version is stamped on by the
    /// workspace when it publishes, because only the workspace knows whether it did.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The builder cannot describe a solution: no workspace, no name, no request, a project
    /// belonging to a different workspace, or two projects with the same identity.
    /// </exception>
    public SolutionSnapshot ToSnapshot()
    {
        if (Workspace.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{nameof(Workspace)} must be set before a solution snapshot can be built.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException(
                $"{nameof(Name)} must be set before a solution snapshot can be built.");
        }

        if (Request is null)
        {
            throw new InvalidOperationException(
                $"{nameof(Request)} must be set before a solution snapshot can be built. A snapshot that " +
                "does not know what it answers cannot be refreshed.");
        }

        if (!Solution.IsEmpty && Solution.Workspace != Workspace)
        {
            throw new InvalidOperationException(
                $"{nameof(Solution)} belongs to workspace '{Solution.Workspace}' but this snapshot is for " +
                $"'{Workspace}'.");
        }

        var seen = new HashSet<ProjectIdentity>();

        foreach (ProjectSnapshot project in Projects)
        {
            if (project.Identity.Workspace != Workspace)
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}' has an identity in workspace '{project.Identity.Workspace}' " +
                    $"but this snapshot is for '{Workspace}'.");
            }

            if (!seen.Add(project.Identity))
            {
                throw new InvalidOperationException(
                    $"Two projects share the identity '{project.Identity}'. Identity is derived from the " +
                    "canonical project path, so this means the same project was added twice.");
            }
        }

        return new SolutionSnapshot(
            Workspace,
            Solution,
            WorkspaceEntryPoint.FromPath(Request.EntryPointPath),
            Name,
            ProviderName,
            Request,
            [.. Projects],
            [.. Diagnostics],
            WorkspaceVersion.None);
    }
}
