namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Everything a <see cref="ProjectWorkspace"/> publishes, in one object.
/// </summary>
/// <remarks>
/// Holding all of it together is what makes a publication a single reference write. A reader takes
/// the current state once and then works entirely inside it, so it can never see a snapshot from
/// one publication beside a version from another — the torn view that separate fields would allow.
/// </remarks>
/// <param name="Snapshot">The published snapshot, or <see langword="null"/> before the first one.</param>
/// <param name="Version">The version of that snapshot.</param>
/// <param name="Request">The request it answers, which is what a refresh repeats.</param>
internal sealed record ProjectWorkspaceState(
    SolutionSnapshot? Snapshot,
    WorkspaceVersion Version,
    WorkspaceLoadRequest? Request)
{
    /// <summary>Gets the state of a workspace that has published nothing.</summary>
    internal static ProjectWorkspaceState Empty { get; } = new(null, WorkspaceVersion.None, null);
}
