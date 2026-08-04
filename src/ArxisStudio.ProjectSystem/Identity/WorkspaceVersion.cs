using System;
using System.Globalization;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// The version of a published snapshot, increasing by one with every successful publication.
/// </summary>
/// <remarks>
/// <para>
/// This is how a consumer decides whether cached UI or analysis state is still current: hold the
/// version alongside whatever was derived from a snapshot, and compare. A failed or cancelled
/// refresh does not advance it, so an unchanged version means the workspace genuinely did not
/// change rather than that nobody looked.
/// </para>
/// <para>
/// Read it from a snapshot rather than from the workspace when both are wanted. A snapshot and its
/// version come from one publication and can never disagree; two separate reads of a live
/// workspace can straddle one.
/// </para>
/// </remarks>
/// <param name="Value">The underlying counter.</param>
public readonly record struct WorkspaceVersion(long Value) : IComparable<WorkspaceVersion>
{
    /// <summary>Gets the version of a workspace that has published nothing, which is <see langword="default"/>.</summary>
    public static WorkspaceVersion None => default;

    /// <summary>Gets the version of the first successful publication.</summary>
    public static WorkspaceVersion Initial => new(1);

    /// <summary>Gets a value indicating whether anything has been published.</summary>
    public bool IsEmpty => Value <= 0;

    /// <summary>Determines whether one version precedes another.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is older.</returns>
    public static bool operator <(WorkspaceVersion left, WorkspaceVersion right) => left.Value < right.Value;

    /// <summary>Determines whether one version precedes or equals another.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is not newer.</returns>
    public static bool operator <=(WorkspaceVersion left, WorkspaceVersion right) => left.Value <= right.Value;

    /// <summary>Determines whether one version follows another.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is newer.</returns>
    public static bool operator >(WorkspaceVersion left, WorkspaceVersion right) => left.Value > right.Value;

    /// <summary>Determines whether one version follows or equals another.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is not older.</returns>
    public static bool operator >=(WorkspaceVersion left, WorkspaceVersion right) => left.Value >= right.Value;

    /// <summary>Returns the next version.</summary>
    /// <returns>The version one greater than this one.</returns>
    public WorkspaceVersion Next() => new(Value + 1);

    /// <summary>Compares this version with another.</summary>
    /// <param name="other">The version to compare with.</param>
    /// <returns>A negative value, zero, or a positive value.</returns>
    public int CompareTo(WorkspaceVersion other) => Value.CompareTo(other.Value);

    /// <summary>Returns the version in a readable form.</summary>
    /// <returns>The counter, or <c>(unpublished)</c> when nothing has been published.</returns>
    public override string ToString() =>
        IsEmpty ? "(unpublished)" : Value.ToString(CultureInfo.InvariantCulture);
}
