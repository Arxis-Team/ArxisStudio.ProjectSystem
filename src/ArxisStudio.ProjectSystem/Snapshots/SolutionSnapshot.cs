using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A complete, immutable picture of everything a workspace has open at one version.
/// </summary>
/// <remarks>
/// <para>
/// This is the object a workspace publishes with a single reference write. A consumer can hold one
/// and enumerate it at leisure while a refresh builds its successor: nothing here changes after
/// publication, so there is no lock to take and no collection-modified exception to handle.
/// </para>
/// <para>
/// <see cref="Version"/> lives on the snapshot rather than only on the workspace, and that is what
/// makes staleness checking safe. Reading <c>workspace.CurrentSnapshot</c> and
/// <c>workspace.CurrentVersion</c> separately is two reads that a publication can happen between;
/// reading <c>snapshot.Version</c> is one value from one publication, which cannot disagree with
/// the snapshot it came from.
/// </para>
/// <para>
/// Like <see cref="ProjectSnapshot"/>, equality is reference equality — see the remarks there for
/// why walking a whole solution to answer <c>Equals</c> would be a trap rather than a feature.
/// </para>
/// </remarks>
public sealed class SolutionSnapshot
{
    private readonly FrozenDictionary<ProjectIdentity, ProjectSnapshot> _byIdentity;
    private readonly FrozenDictionary<CanonicalPath, ProjectSnapshot> _byPath;
    private readonly FrozenDictionary<ProjectIdentity, SolutionFolder> _folderByProject;

    internal SolutionSnapshot(
        WorkspaceIdentity workspace,
        SolutionIdentity solution,
        WorkspaceEntryPoint entryPoint,
        string name,
        string? providerName,
        WorkspaceLoadRequest request,
        ImmutableArray<ProjectSnapshot> projects,
        ImmutableArray<SolutionFolder> folders,
        ImmutableArray<string> configurations,
        ImmutableArray<string> platforms,
        ImmutableArray<ProjectDiagnostic> diagnostics,
        WorkspaceVersion version)
    {
        Workspace = workspace;
        Solution = solution;
        EntryPoint = entryPoint;
        Name = name;
        ProviderName = providerName;
        Request = request;
        Projects = projects;
        Folders = folders;
        Configurations = configurations;
        Platforms = platforms;
        Diagnostics = diagnostics;
        Version = version;

        Dictionary<ProjectIdentity, ProjectSnapshot> byIdentity = [];
        Dictionary<CanonicalPath, ProjectSnapshot> byPath = [];

        foreach (ProjectSnapshot project in projects)
        {
            byIdentity[project.Identity] = project;
            byPath[project.ProjectFilePath] = project;
        }

        Dictionary<ProjectIdentity, SolutionFolder> folderByProject = [];

        foreach (SolutionFolder folder in folders)
        {
            foreach (ProjectIdentity member in folder.Projects)
            {
                folderByProject[member] = folder;
            }
        }

        _byIdentity = byIdentity.ToFrozenDictionary();
        _byPath = byPath.ToFrozenDictionary();
        _folderByProject = folderByProject.ToFrozenDictionary();
    }

    /// <summary>Gets the workspace this snapshot belongs to.</summary>
    public WorkspaceIdentity Workspace { get; }

    /// <summary>
    /// Gets the solution's identity, or <see cref="SolutionIdentity.None"/> when the workspace was
    /// opened on a standalone project.
    /// </summary>
    public SolutionIdentity Solution { get; }

    /// <summary>Gets what was opened.</summary>
    public WorkspaceEntryPoint EntryPoint { get; }

    /// <summary>Gets the display name of the solution, or of the project when there is no solution.</summary>
    public string Name { get; }

    /// <summary>Gets the name of the provider that produced this snapshot.</summary>
    public string? ProviderName { get; }

    /// <summary>Gets the request this snapshot answers.</summary>
    public WorkspaceLoadRequest Request { get; }

    /// <summary>
    /// Gets the version this snapshot was published at, or <see cref="WorkspaceVersion.None"/> if
    /// it has not been published.
    /// </summary>
    public WorkspaceVersion Version { get; }

    /// <summary>Gets the projects, in the order the provider supplied them.</summary>
    public ImmutableArray<ProjectSnapshot> Projects { get; }

    /// <summary>
    /// Gets the solution's folders, which organise the projects for display and exist nowhere on
    /// disk. Empty when the entry point was a standalone project.
    /// </summary>
    public ImmutableArray<SolutionFolder> Folders { get; }

    /// <summary>Gets the configurations the solution declares, such as <c>Debug</c> and <c>Release</c>.</summary>
    public ImmutableArray<string> Configurations { get; }

    /// <summary>Gets the platforms the solution declares, such as <c>Any CPU</c>.</summary>
    public ImmutableArray<string> Platforms { get; }

    /// <summary>Gets the diagnostics raised about the solution as a whole.</summary>
    /// <remarks>
    /// Diagnostics about a particular project live on that project's snapshot. A caller that wants
    /// everything at once should read <see cref="WorkspaceLoadResult.Diagnostics"/>, which is the
    /// flattened form.
    /// </remarks>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether any diagnostic, anywhere in this snapshot, is an error.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (ProjectDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.IsError)
                {
                    return true;
                }
            }

            foreach (ProjectSnapshot project in Projects)
            {
                if (project.HasErrors)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Finds a project by identity.</summary>
    /// <param name="identity">The identity to look for.</param>
    /// <param name="project">The project, when it is in this snapshot.</param>
    /// <returns><see langword="true"/> when the project was found.</returns>
    public bool TryGetProject(ProjectIdentity identity, [NotNullWhen(true)] out ProjectSnapshot? project) =>
        _byIdentity.TryGetValue(identity, out project);

    /// <summary>Finds a project by the canonical path of its project file.</summary>
    /// <param name="projectFilePath">The path to look for.</param>
    /// <param name="project">The project, when it is in this snapshot.</param>
    /// <returns><see langword="true"/> when the project was found.</returns>
    public bool TryGetProject(CanonicalPath projectFilePath, [NotNullWhen(true)] out ProjectSnapshot? project) =>
        _byPath.TryGetValue(projectFilePath, out project);

    /// <summary>Finds the folder a project sits in.</summary>
    /// <param name="project">The project to look for.</param>
    /// <param name="folder">The folder, when the project is in one.</param>
    /// <returns><see langword="true"/> when the project sits in a folder rather than at the root.</returns>
    public bool TryGetFolder(ProjectIdentity project, [NotNullWhen(true)] out SolutionFolder? folder) =>
        _folderByProject.TryGetValue(project, out folder);

    /// <summary>Returns the solution name and its project count.</summary>
    /// <returns>Something like <c>App (3 projects, version 2)</c>.</returns>
    public override string ToString() =>
        $"{Name} ({Projects.Length} project{(Projects.Length == 1 ? string.Empty : "s")}, version {Version})";

    /// <summary>
    /// Returns a copy stamped with a publication version.
    /// </summary>
    /// <remarks>
    /// Copies one object and no arrays: the projects, diagnostics and lookups are all immutable and
    /// are shared with the original. Internal because stamping a version is the workspace's job —
    /// a snapshot that could version itself could claim to have been published when it had not.
    /// </remarks>
    internal SolutionSnapshot WithVersion(WorkspaceVersion version) =>
        version == Version
            ? this
            : new SolutionSnapshot(
                Workspace, Solution, EntryPoint, Name, ProviderName, Request,
                Projects, Folders, Configurations, Platforms, Diagnostics, version);
}
