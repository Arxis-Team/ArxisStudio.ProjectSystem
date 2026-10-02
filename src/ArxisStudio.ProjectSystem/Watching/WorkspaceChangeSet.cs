using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ArxisStudio.ProjectSystem;

/// <summary>An item whose file's contents changed: the project that declares it, and the item.</summary>
/// <param name="Project">The project that declares the item — the first in snapshot order, when several do.</param>
/// <param name="Item">The item, as the snapshot has it.</param>
public sealed record ProjectItemChange(ProjectIdentity Project, ProjectItem Item);

/// <summary>A file a project declares, renamed.</summary>
/// <param name="OldPath">Where the file was — the path the snapshot knows.</param>
/// <param name="NewPath">Where it is now.</param>
public readonly record struct FileRename(CanonicalPath OldPath, CanonicalPath NewPath);

/// <summary>
/// What a batch of file changes means for a workspace: which evaluations are stale, which items'
/// contents changed, which projects may include different files now, which items moved, and whether
/// changes were lost.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SolutionSnapshot.Classify"/>'s answer, and the reason it exists beside
/// <see cref="SolutionSnapshot.Invalidate"/>
/// (<see href="../../docs/adr/0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md">ADR 0025</see>).
/// A host that only re-evaluates needs only <see cref="Invalidation"/>. A host that shows files — a
/// designer with forms open — needs the rest: a saved form is a document to reload, not a project to
/// evaluate; a new form changes what a project includes, which a re-evaluation of that project
/// answers; a renamed one moves whatever had it open.
/// </para>
/// <para>
/// Immutable, like everything this library publishes. <see cref="IsEmpty"/> is the common case — a
/// build writing its output, an editor's own state — and costs nothing to act on.
/// </para>
/// </remarks>
public sealed class WorkspaceChangeSet
{
    internal WorkspaceChangeSet(
        WorkspaceInvalidation invalidation,
        ImmutableArray<ProjectItemChange> itemsEdited,
        ImmutableArray<ProjectIdentity> membershipChanged,
        ImmutableArray<FileRename> renames,
        bool requiresRescan)
    {
        Invalidation = invalidation;
        ItemsEdited = itemsEdited;
        MembershipChanged = membershipChanged;
        Renames = renames;
        RequiresRescan = requiresRescan;
    }

    /// <summary>Gets the answer for a batch that changed nothing anybody depends on.</summary>
    public static WorkspaceChangeSet None { get; } = new(WorkspaceInvalidation.None, [], [], [], false);

    /// <summary>
    /// Gets which evaluations the batch made stale: a project file or an import changed, appeared or
    /// went away, or the solution itself changed.
    /// </summary>
    /// <remarks>The same answer <see cref="SolutionSnapshot.Invalidate"/> gives for the batch's paths.</remarks>
    public WorkspaceInvalidation Invalidation { get; }

    /// <summary>
    /// Gets the items whose files' contents changed, in the order the batch named them.
    /// </summary>
    /// <remarks>
    /// Nothing about the project changed, only a file it declares: a document to read again, not a
    /// project to evaluate. A file saved through a temporary file and two renames lands here once,
    /// because <see cref="FileChangeCoalescer"/> delivers it as one change.
    /// </remarks>
    public ImmutableArray<ProjectItemChange> ItemsEdited { get; }

    /// <summary>
    /// Gets the projects whose set of files may be different now, in snapshot order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file appeared, went away or was renamed inside a project's directory, outside its build
    /// directories (<see cref="ProjectSnapshot.BuildDirectories"/>) and outside dot-directories —
    /// where a project's globs reach. Whether the glob takes it is the evaluation's question, so
    /// these projects are to be evaluated again; that is far cheaper than guessing wrong.
    /// </para>
    /// <para>
    /// A project already in <see cref="Invalidation"/> is not listed again, and nothing is listed when
    /// the entry point changed: everything is evaluated again then.
    /// </para>
    /// </remarks>
    public ImmutableArray<ProjectIdentity> MembershipChanged { get; }

    /// <summary>Gets the declared files that were renamed, in the order the batch named them.</summary>
    /// <remarks>
    /// Whatever had the old path open follows the new one. The projects that declare them are in
    /// <see cref="MembershipChanged"/> as well: the item list itself is now different.
    /// </remarks>
    public ImmutableArray<FileRename> Renames { get; }

    /// <summary>
    /// Gets a value indicating whether changes were lost and everything has to be looked at again.
    /// </summary>
    public bool RequiresRescan { get; }

    /// <summary>Gets a value indicating whether nothing needs doing.</summary>
    public bool IsEmpty =>
        Invalidation.IsEmpty && ItemsEdited.IsEmpty && MembershipChanged.IsEmpty && Renames.IsEmpty && !RequiresRescan;

    /// <summary>
    /// Gets the projects to evaluate again: the stale ones, then those whose files may be different.
    /// </summary>
    /// <remarks>
    /// Empty when the entry point changed — then everything is — and when nothing needs evaluating.
    /// </remarks>
    public ImmutableArray<ProjectIdentity> ProjectsToEvaluate =>
        [.. Invalidation.Projects.Concat(MembershipChanged)];

    /// <summary>Returns what the batch amounts to.</summary>
    /// <returns>Something like <c>1 item edited, membership of 1 project</c>, or <c>None</c>.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return "None";
        }

        var parts = new List<string>();

        if (RequiresRescan)
        {
            parts.Add("rescan");
        }

        if (!Invalidation.IsEmpty)
        {
            parts.Add(Invalidation.ToString());
        }

        if (!ItemsEdited.IsEmpty)
        {
            parts.Add($"{ItemsEdited.Length} item{(ItemsEdited.Length == 1 ? string.Empty : "s")} edited");
        }

        if (!MembershipChanged.IsEmpty)
        {
            parts.Add($"membership of {MembershipChanged.Length} project{(MembershipChanged.Length == 1 ? string.Empty : "s")}");
        }

        if (!Renames.IsEmpty)
        {
            parts.Add($"{Renames.Length} rename{(Renames.Length == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts);
    }
}
