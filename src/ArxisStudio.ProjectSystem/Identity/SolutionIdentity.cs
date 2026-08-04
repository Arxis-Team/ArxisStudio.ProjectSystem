using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Identifies the solution a workspace was opened on, where there is one.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the workspace and the canonical path of the solution file, on the same reasoning
/// as <see cref="ProjectIdentity"/> — and, like it, deliberately not a <c>record struct</c>, so
/// that no <c>with</c> expression can attach it to a workspace that never issued it.
/// </para>
/// <para>
/// A workspace opened on a standalone project has no solution, and its snapshot carries
/// <see cref="None"/>. That is the distinction this type exists to make plain: absent because
/// there is no solution, rather than absent because nothing has loaded yet.
/// </para>
/// </remarks>
public readonly struct SolutionIdentity : IEquatable<SolutionIdentity>, IComparable<SolutionIdentity>
{
    private SolutionIdentity(WorkspaceIdentity workspace, CanonicalPath solutionFilePath)
    {
        Workspace = workspace;
        SolutionFilePath = solutionFilePath;
    }

    /// <summary>Gets the absent identity, which is also <see langword="default"/>.</summary>
    public static SolutionIdentity None => default;

    /// <summary>Gets the workspace this solution was loaded into.</summary>
    public WorkspaceIdentity Workspace { get; }

    /// <summary>Gets the canonical path of the solution file.</summary>
    public CanonicalPath SolutionFilePath { get; }

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsEmpty => Workspace.IsEmpty || SolutionFilePath.IsEmpty;

    /// <summary>Determines whether two identities denote the same solution.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(SolutionIdentity left, SolutionIdentity right) => left.Equals(right);

    /// <summary>Determines whether two identities denote different solutions.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(SolutionIdentity left, SolutionIdentity right) => !left.Equals(right);

    /// <summary>Determines whether one identity sorts before another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts first.</returns>
    public static bool operator <(SolutionIdentity left, SolutionIdentity right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether one identity sorts before or equal to another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort after.</returns>
    public static bool operator <=(SolutionIdentity left, SolutionIdentity right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether one identity sorts after another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts last.</returns>
    public static bool operator >(SolutionIdentity left, SolutionIdentity right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether one identity sorts after or equal to another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort before.</returns>
    public static bool operator >=(SolutionIdentity left, SolutionIdentity right) => left.CompareTo(right) >= 0;

    /// <summary>Creates an identity for a solution in a workspace.</summary>
    /// <param name="workspace">The workspace the solution belongs to.</param>
    /// <param name="solutionFilePath">The canonical path of the solution file.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="workspace"/> or <paramref name="solutionFilePath"/> is absent.
    /// </exception>
    public static SolutionIdentity Create(WorkspaceIdentity workspace, CanonicalPath solutionFilePath)
    {
        if (workspace.IsEmpty)
        {
            throw new ArgumentException("A solution identity needs the workspace it belongs to.", nameof(workspace));
        }

        if (solutionFilePath.IsEmpty)
        {
            throw new ArgumentException(
                "A solution identity needs the canonical path of its solution file.",
                nameof(solutionFilePath));
        }

        return new SolutionIdentity(workspace, solutionFilePath);
    }

    /// <summary>Determines whether this identity equals another.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public bool Equals(SolutionIdentity other) =>
        Workspace == other.Workspace && SolutionFilePath == other.SolutionFilePath;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is SolutionIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Workspace, SolutionFilePath);

    /// <summary>Compares this identity with another, by solution file and then by workspace.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns>A negative value, zero, or a positive value.</returns>
    public int CompareTo(SolutionIdentity other)
    {
        int byPath = SolutionFilePath.CompareTo(other.SolutionFilePath);

        return byPath != 0
            ? byPath
            : Workspace.Value.CompareTo(other.Workspace.Value);
    }

    /// <summary>Returns the solution file name and a short form of its workspace.</summary>
    /// <returns>Something like <c>App.sln [3f2a1b2c]</c>.</returns>
    public override string ToString() =>
        IsEmpty
            ? "(none)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{SolutionFilePath.FileName} [{Workspace.Value.ToString("N", CultureInfo.InvariantCulture)[..8]}]");
}
