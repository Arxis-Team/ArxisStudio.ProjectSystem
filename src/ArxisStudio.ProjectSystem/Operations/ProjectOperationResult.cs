using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>How an operation ended.</summary>
public enum ProjectOperationStatus
{
    /// <summary>The work was done. There may still be warnings.</summary>
    Succeeded = 0,

    /// <summary>The work was not done, and the diagnostics say why.</summary>
    Failed = 1,
}

/// <summary>
/// What an operation produced: whether it worked, and everything it had to say.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> is computed from the diagnostics rather than supplied beside them, on the
/// same reasoning as <see cref="WorkspaceLoadResult"/> and for the same reason: a stored outcome
/// next to stored evidence is two facts that can disagree, and the first time they do, a consumer
/// branching on the outcome silently ignores the evidence. Here the rule is simple — an operation
/// failed exactly when something reported an error.
/// </para>
/// <para>
/// That rule holds because build engines make it hold: a build that fails logs an error, and one
/// that logs an error fails. A provider whose engine somehow fails without saying why must
/// synthesise a diagnostic rather than report an unexplained failure, which
/// <see cref="Failed(IEnumerable{ProjectDiagnostic})"/> enforces.
/// </para>
/// <para>
/// Cancellation is not a status. It is <see cref="OperationCanceledException"/>, which keeps these
/// two values genuinely exhaustive.
/// </para>
/// </remarks>
public sealed class ProjectOperationResult
{
    private ProjectOperationResult(
        ProjectOperationStatus status,
        ImmutableArray<ProjectDiagnostic> diagnostics)
    {
        Status = status;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets whether the work was done.</summary>
    public ProjectOperationStatus Status { get; }

    /// <summary>Gets everything the operation reported.</summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether anything reported an error.</summary>
    public bool HasErrors => Status == ProjectOperationStatus.Failed;

    /// <summary>Creates a result for an operation that did its work.</summary>
    /// <param name="diagnostics">Anything it had to say. Warnings are fine; an error is not.</param>
    /// <returns>A succeeded result.</returns>
    /// <exception cref="ArgumentException">
    /// A diagnostic is an error. An operation that reported one did not succeed, and letting the
    /// two disagree is the thing this type exists to prevent.
    /// </exception>
    public static ProjectOperationResult Succeeded(IEnumerable<ProjectDiagnostic>? diagnostics = null)
    {
        ImmutableArray<ProjectDiagnostic> all = Collect(diagnostics);

        return HasAnyError(all)
            ? throw new ArgumentException(
                "An operation that reported an error did not succeed.", nameof(diagnostics))
            : new ProjectOperationResult(ProjectOperationStatus.Succeeded, all);
    }

    /// <summary>Creates a result for an operation that did not do its work.</summary>
    /// <param name="diagnostics">Why it did not. At least one must be an error.</param>
    /// <returns>A failed result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// No diagnostic is an error. A failure nobody can explain tells a consumer nothing, so it is
    /// refused rather than represented.
    /// </exception>
    public static ProjectOperationResult Failed(IEnumerable<ProjectDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        ImmutableArray<ProjectDiagnostic> all = Collect(diagnostics);

        return HasAnyError(all)
            ? new ProjectOperationResult(ProjectOperationStatus.Failed, all)
            : throw new ArgumentException(
                "A failed operation must carry at least one error diagnostic explaining why.",
                nameof(diagnostics));
    }

    /// <summary>Creates a failed result from a single diagnostic.</summary>
    /// <param name="diagnostic">Why it failed. Must be an error.</param>
    /// <returns>A failed result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostic"/> is not an error.</exception>
    public static ProjectOperationResult Failed(ProjectDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return Failed([diagnostic]);
    }

    /// <summary>Returns the status and the diagnostic count.</summary>
    /// <returns>A readable representation of the result.</returns>
    public override string ToString() =>
        Diagnostics.Length == 0
            ? Status.ToString()
            : $"{Status} ({Diagnostics.Length} diagnostic{(Diagnostics.Length == 1 ? string.Empty : "s")})";

    private static ImmutableArray<ProjectDiagnostic> Collect(IEnumerable<ProjectDiagnostic>? diagnostics)
    {
        if (diagnostics is null)
        {
            return [];
        }

        ImmutableArray<ProjectDiagnostic>.Builder collected = ImmutableArray.CreateBuilder<ProjectDiagnostic>();

        foreach (ProjectDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic is not null)
            {
                collected.Add(diagnostic);
            }
        }

        return collected.ToImmutable();
    }

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
