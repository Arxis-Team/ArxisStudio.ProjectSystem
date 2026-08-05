using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// What a set of file changes made stale, and why.
/// </summary>
/// <remarks>
/// <para>
/// The answer to "something on disk changed — does it matter?", computed against a snapshot rather
/// than guessed at. A host that watches files turns a batch of changes into one of these and acts on
/// the scope; a scope of <see cref="WorkspaceInvalidationScope.None"/> is the common case and costs
/// nothing to act on.
/// </para>
/// <para>
/// Like everything else published by this library, it is immutable and computed — the scope is
/// derived from the evidence rather than stored beside it, so it cannot disagree with the projects
/// and causes it carries.
/// </para>
/// </remarks>
public sealed class WorkspaceInvalidation
{
    private WorkspaceInvalidation(
        WorkspaceInvalidationScope scope,
        ImmutableArray<ProjectIdentity> projects,
        ImmutableArray<CanonicalPath> causes)
    {
        Scope = scope;
        Projects = projects;
        Causes = causes;
    }

    /// <summary>Gets the answer for "nothing that matters changed".</summary>
    public static WorkspaceInvalidation None { get; } =
        new(WorkspaceInvalidationScope.None, [], []);

    /// <summary>Gets how much was invalidated.</summary>
    public WorkspaceInvalidationScope Scope { get; }

    /// <summary>
    /// Gets the projects that must be evaluated again, in the order they appear in the snapshot.
    /// </summary>
    /// <remarks>
    /// Empty when <see cref="Scope"/> is <see cref="WorkspaceInvalidationScope.EntryPoint"/>, and
    /// that is not an oversight: if the entry point changed, the projects that will exist afterwards
    /// are not known until it has been read again, so listing the ones that used to exist would be
    /// answering a different question.
    /// </remarks>
    public ImmutableArray<ProjectIdentity> Projects { get; }

    /// <summary>Gets the changed files that actually mattered, in the order they were given.</summary>
    /// <remarks>
    /// A subset of what was passed in — the changes nothing depends on are dropped. This is what
    /// lets a host explain itself: "reloading because Directory.Build.props changed" is a far better
    /// thing to log than "reloading".
    /// </remarks>
    public ImmutableArray<CanonicalPath> Causes { get; }

    /// <summary>Gets a value indicating whether nothing needs doing.</summary>
    public bool IsEmpty => Scope == WorkspaceInvalidationScope.None;

    /// <summary>Returns the scope and what caused it.</summary>
    /// <returns>Something like <c>Projects: 2 stale, caused by 1 change</c>.</returns>
    public override string ToString() => Scope switch
    {
        WorkspaceInvalidationScope.None => "None",
        WorkspaceInvalidationScope.EntryPoint =>
            $"EntryPoint, caused by {Describe(Causes.Length, "change")}",
        _ => $"Projects: {Describe(Projects.Length, "stale project")}, " +
            $"caused by {Describe(Causes.Length, "change")}",
    };

    internal static WorkspaceInvalidation ForEntryPoint(ImmutableArray<CanonicalPath> causes) =>
        new(WorkspaceInvalidationScope.EntryPoint, [], causes);

    internal static WorkspaceInvalidation ForProjects(
        ImmutableArray<ProjectIdentity> projects,
        ImmutableArray<CanonicalPath> causes) =>
        projects.IsEmpty ? None : new(WorkspaceInvalidationScope.Projects, projects, causes);

    private static string Describe(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
