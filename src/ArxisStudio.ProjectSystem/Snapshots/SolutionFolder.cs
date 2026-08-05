using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A folder in a solution's tree, which organises projects without existing on disk.
/// </summary>
/// <remarks>
/// <para>
/// Solution folders are a display concern and nothing more: they group projects in a tree, they may
/// nest, and they have no directory behind them. That is why <see cref="Path"/> is a
/// <see cref="string"/> rather than a <see cref="CanonicalPath"/> — it names a position in the
/// solution, not a place on the file system, and the whole point of
/// <see cref="CanonicalPath"/> is that it does not answer questions like this one.
/// </para>
/// <para>
/// Membership lives here rather than on <see cref="ProjectSnapshot"/> because it is a fact about the
/// solution rather than about the project: the same project file can sit in different folders in
/// two different solutions, and a project snapshot that claimed one would be wrong in the other.
/// <see cref="SolutionSnapshot.TryGetFolder(ProjectIdentity, out SolutionFolder?)"/> is the way
/// back.
/// </para>
/// </remarks>
public sealed record SolutionFolder
{
    /// <summary>Gets the folder's display name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the folder's position in the solution tree, as a slash-separated path such as
    /// <c>/src/Libraries/</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Gets the projects directly inside this folder, not counting nested folders.</summary>
    public ImmutableArray<ProjectIdentity> Projects
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Returns the folder's path.</summary>
    /// <returns>Something like <c>/src/Libraries/</c>.</returns>
    public override string ToString() => Path;
}
