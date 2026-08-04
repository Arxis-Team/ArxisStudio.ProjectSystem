using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// What a load produced: a snapshot, diagnostics, or both.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>Success</c> property, and that is the whole design. A stored boolean beside a
/// stored snapshot is two facts that can disagree, and they disagree the first time a solution
/// loads with one broken project — the natural implementation reports success while holding an
/// error. So the authoritative state here is the pair <em>(is there a snapshot?, is there an error
/// anywhere?)</em>, <see cref="Status"/> is a total function of that pair, and everything else is
/// derived from <see cref="Status"/>:
/// </para>
/// <list type="table">
///   <listheader><term>Snapshot</term><term>Any error</term><description>Status</description></listheader>
///   <item><term>yes</term><term>no</term><description><see cref="WorkspaceLoadStatus.Succeeded"/></description></item>
///   <item><term>yes</term><term>yes</term><description><see cref="WorkspaceLoadStatus.SucceededWithErrors"/></description></item>
///   <item><term>no</term><term>yes</term><description><see cref="WorkspaceLoadStatus.Failed"/></description></item>
///   <item><term>no</term><term>no</term><description>cannot be constructed</description></item>
/// </list>
/// <para>
/// "Any error anywhere" is meant literally. <see cref="Success"/> flattens the snapshot's own
/// diagnostics and every project's diagnostics into <see cref="Diagnostics"/>, so a failed project
/// inside an otherwise-loaded solution cannot produce a <see cref="WorkspaceLoadStatus.Succeeded"/>
/// result. The cost is that a project-level diagnostic appears twice — once on its
/// <see cref="ProjectSnapshot"/> and once here — which is what an error-list consumer wants anyway.
/// </para>
/// <para>
/// <see cref="Snapshot"/> is <see langword="null"/> if and only if
/// <see cref="Status"/> is <see cref="WorkspaceLoadStatus.Failed"/>.
/// </para>
/// </remarks>
public sealed class WorkspaceLoadResult
{
    private WorkspaceLoadResult(
        WorkspaceLoadStatus status,
        SolutionSnapshot? snapshot,
        ImmutableArray<ProjectDiagnostic> diagnostics)
    {
        Status = status;
        Snapshot = snapshot;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets how the load ended.</summary>
    public WorkspaceLoadStatus Status { get; }

    /// <summary>Gets the snapshot, or <see langword="null"/> when the load failed.</summary>
    public SolutionSnapshot? Snapshot { get; }

    /// <summary>Gets every diagnostic the load produced, including those on individual projects.</summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether there is a usable snapshot.</summary>
    public bool HasSnapshot => Status != WorkspaceLoadStatus.Failed;

    /// <summary>Gets a value indicating whether anything went wrong.</summary>
    public bool HasErrors => Status != WorkspaceLoadStatus.Succeeded;

    /// <summary>Creates a result carrying a snapshot.</summary>
    /// <param name="snapshot">The snapshot the load produced.</param>
    /// <param name="diagnostics">
    /// Diagnostics beyond those already on the snapshot and its projects, which are collected
    /// automatically. May be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A result whose <see cref="Status"/> is <see cref="WorkspaceLoadStatus.Succeeded"/> or
    /// <see cref="WorkspaceLoadStatus.SucceededWithErrors"/>, decided by whether any collected
    /// diagnostic is an error.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static WorkspaceLoadResult Success(
        SolutionSnapshot snapshot,
        IEnumerable<ProjectDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ImmutableArray<ProjectDiagnostic>.Builder collected = ImmutableArray.CreateBuilder<ProjectDiagnostic>();

        collected.AddRange(snapshot.Diagnostics);

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            collected.AddRange(project.Diagnostics);
        }

        if (diagnostics is not null)
        {
            foreach (ProjectDiagnostic diagnostic in diagnostics)
            {
                if (diagnostic is not null)
                {
                    collected.Add(diagnostic);
                }
            }
        }

        ImmutableArray<ProjectDiagnostic> all = collected.ToImmutable();

        return new WorkspaceLoadResult(
            HasAnyError(all) ? WorkspaceLoadStatus.SucceededWithErrors : WorkspaceLoadStatus.Succeeded,
            snapshot,
            all);
    }

    /// <summary>Creates a result carrying no snapshot.</summary>
    /// <param name="diagnostics">Why the load produced nothing. At least one must be an error.</param>
    /// <returns>A result whose <see cref="Status"/> is <see cref="WorkspaceLoadStatus.Failed"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// No diagnostic is an error. A failure nobody can explain is the fourth cell of the table on
    /// this type, and it is refused rather than represented: a consumer shown "it failed" with
    /// nothing to display has been told nothing.
    /// </exception>
    public static WorkspaceLoadResult Failure(IEnumerable<ProjectDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        ImmutableArray<ProjectDiagnostic> all = [.. diagnostics];

        if (!HasAnyError(all))
        {
            throw new ArgumentException(
                "A failed load must carry at least one error diagnostic explaining why.",
                nameof(diagnostics));
        }

        return new WorkspaceLoadResult(WorkspaceLoadStatus.Failed, snapshot: null, all);
    }

    /// <summary>Creates a failed result from a single diagnostic.</summary>
    /// <param name="diagnostic">Why the load produced nothing. Must be an error.</param>
    /// <returns>A result whose <see cref="Status"/> is <see cref="WorkspaceLoadStatus.Failed"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostic"/> is not an error.</exception>
    public static WorkspaceLoadResult Failure(ProjectDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return Failure([diagnostic]);
    }

    /// <summary>Returns the status and the diagnostic count.</summary>
    /// <returns>A readable representation of the result.</returns>
    public override string ToString() =>
        Diagnostics.Length == 0
            ? Status.ToString()
            : $"{Status} ({Diagnostics.Length} diagnostic{(Diagnostics.Length == 1 ? string.Empty : "s")})";

    /// <summary>
    /// Returns a copy carrying a snapshot that differs only by its publication version.
    /// </summary>
    /// <remarks>
    /// Used by the workspace after it stamps a version on publication. The status and diagnostics
    /// are carried across unchanged, because stamping a version cannot change whether the load
    /// succeeded.
    /// </remarks>
    internal WorkspaceLoadResult WithSnapshot(SolutionSnapshot snapshot) =>
        ReferenceEquals(snapshot, Snapshot) ? this : new WorkspaceLoadResult(Status, snapshot, Diagnostics);

    private static bool HasAnyError(ImmutableArray<ProjectDiagnostic> diagnostics)
    {
        foreach (ProjectDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.IsError)
            {
                return true;
            }
        }

        return false;
    }
}
