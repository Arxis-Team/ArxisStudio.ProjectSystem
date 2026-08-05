namespace ArxisStudio.ProjectSystem;

/// <summary>How much of a workspace a set of file changes made stale.</summary>
public enum WorkspaceInvalidationScope
{
    /// <summary>Nothing changed that any open project depends on.</summary>
    None = 0,

    /// <summary>
    /// Some projects are stale, and the rest of the workspace is not. Which projects those are is in
    /// <see cref="WorkspaceInvalidation.Projects"/>.
    /// </summary>
    Projects = 1,

    /// <summary>
    /// The entry point itself changed, so the set of projects may no longer be the same and nothing
    /// short of loading it again can say what is open.
    /// </summary>
    EntryPoint = 2,
}
