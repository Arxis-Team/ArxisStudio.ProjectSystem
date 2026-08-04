using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Identifies one project inside one workspace.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the workspace it belongs to and the canonical path of its project file, which
/// makes the determinism the specification asks for a property of the type rather than a
/// behaviour something has to maintain: a provider reloading the same project file in the same
/// workspace produces an equal identity because it cannot produce anything else. There is no
/// registry to consult and none to get wrong, and a provider can mint identities from values it
/// already has on the request — so it never calls back into the workspace to ask for one.
/// </para>
/// <para>
/// Deliberately <b>not</b> a <c>record struct</c>. A generated <c>with</c> expression would let a
/// caller write <c>identity with { Workspace = somebodyElses }</c> and forge an identity for a
/// workspace that never issued it. Nothing here needs non-destructive mutation.
/// </para>
/// <para>
/// Stability follows the workspace exactly: a <see cref="ProjectIdentity"/> is stable for as long
/// as its <see cref="Workspace"/> is, and since a new workspace mints a new
/// <see cref="WorkspaceIdentity"/>, identities do not survive a process restart. Do not persist
/// them. A <see cref="ProjectSystem.CanonicalPath"/> is what to store instead.
/// </para>
/// <para>
/// Equality with a <see cref="SolutionIdentity"/> is impossible rather than merely false: they are
/// different types, and the compiler rejects the comparison.
/// </para>
/// </remarks>
public readonly struct ProjectIdentity : IEquatable<ProjectIdentity>, IComparable<ProjectIdentity>
{
    private ProjectIdentity(WorkspaceIdentity workspace, CanonicalPath projectFilePath)
    {
        Workspace = workspace;
        ProjectFilePath = projectFilePath;
    }

    /// <summary>Gets the absent identity, which is also <see langword="default"/>.</summary>
    public static ProjectIdentity None => default;

    /// <summary>Gets the workspace this project was loaded into.</summary>
    public WorkspaceIdentity Workspace { get; }

    /// <summary>Gets the canonical path of the project file.</summary>
    public CanonicalPath ProjectFilePath { get; }

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsEmpty => Workspace.IsEmpty || ProjectFilePath.IsEmpty;

    /// <summary>Determines whether two identities denote the same project.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(ProjectIdentity left, ProjectIdentity right) => left.Equals(right);

    /// <summary>Determines whether two identities denote different projects.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(ProjectIdentity left, ProjectIdentity right) => !left.Equals(right);

    /// <summary>Determines whether one identity sorts before another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts first.</returns>
    public static bool operator <(ProjectIdentity left, ProjectIdentity right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether one identity sorts before or equal to another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort after.</returns>
    public static bool operator <=(ProjectIdentity left, ProjectIdentity right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether one identity sorts after another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts last.</returns>
    public static bool operator >(ProjectIdentity left, ProjectIdentity right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether one identity sorts after or equal to another.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort before.</returns>
    public static bool operator >=(ProjectIdentity left, ProjectIdentity right) => left.CompareTo(right) >= 0;

    /// <summary>Creates an identity for a project in a workspace.</summary>
    /// <param name="workspace">The workspace the project belongs to.</param>
    /// <param name="projectFilePath">The canonical path of the project file.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="workspace"/> or <paramref name="projectFilePath"/> is absent. A half-formed
    /// identity would compare equal to other half-formed ones, so it is refused rather than made.
    /// </exception>
    public static ProjectIdentity Create(WorkspaceIdentity workspace, CanonicalPath projectFilePath)
    {
        if (workspace.IsEmpty)
        {
            throw new ArgumentException("A project identity needs the workspace it belongs to.", nameof(workspace));
        }

        if (projectFilePath.IsEmpty)
        {
            throw new ArgumentException(
                "A project identity needs the canonical path of its project file.",
                nameof(projectFilePath));
        }

        return new ProjectIdentity(workspace, projectFilePath);
    }

    /// <summary>Determines whether this identity equals another.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public bool Equals(ProjectIdentity other) =>
        Workspace == other.Workspace && ProjectFilePath == other.ProjectFilePath;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ProjectIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Workspace, ProjectFilePath);

    /// <summary>Compares this identity with another, by project file and then by workspace.</summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns>A negative value, zero, or a positive value.</returns>
    public int CompareTo(ProjectIdentity other)
    {
        int byPath = ProjectFilePath.CompareTo(other.ProjectFilePath);

        return byPath != 0
            ? byPath
            : Workspace.Value.CompareTo(other.Workspace.Value);
    }

    /// <summary>Returns the project file name and a short form of its workspace.</summary>
    /// <returns>Something like <c>App.csproj [3f2a1b2c]</c>.</returns>
    public override string ToString() =>
        IsEmpty
            ? "(none)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{ProjectFilePath.FileName} [{Workspace.Value.ToString("N", CultureInfo.InvariantCulture)[..8]}]");
}
