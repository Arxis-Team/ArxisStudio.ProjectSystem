namespace ArxisStudio.ProjectSystem;

/// <summary>
/// One item in a project: a source file, a content file, a folder, or anything else a provider
/// evaluated.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ItemType"/> is a <see cref="string"/> and not an enum on purpose. MSBuild item types
/// are user-extensible, so an enum would need an <c>Other</c> member and a second field carrying
/// the real name — and two fields that can disagree is exactly the shape this library avoids
/// elsewhere. <see cref="Origin"/> is an enum because its set really is closed.
/// </para>
/// <para>
/// <see cref="Include"/> and <see cref="FullPath"/> are different things and both are kept.
/// <see cref="Include"/> is what the project file says, which may be relative, a glob, or not a
/// path at all; <see cref="FullPath"/> is the resolved location, and is
/// <see cref="CanonicalPath.None"/> for an item that does not name a file.
/// </para>
/// <para>
/// Enumerating items never touches the file system. An item whose file has since been deleted is
/// still in the snapshot it was captured in, which is what makes a snapshot a snapshot.
/// </para>
/// </remarks>
public sealed record ProjectItem
{
    /// <summary>Gets the MSBuild item type, such as <c>Compile</c>. Compared case-insensitively.</summary>
    public required string ItemType { get; init; }

    /// <summary>Gets the item's identity as the project declares it, which need not be a path.</summary>
    public required string Include { get; init; }

    /// <summary>Gets the resolved location, or <see cref="CanonicalPath.None"/> when the item names no file.</summary>
    public CanonicalPath FullPath { get; init; }

    /// <summary>Gets the item's logical path in the project tree, when it differs from its location on disk.</summary>
    public string? Link { get; init; }

    /// <summary>Gets the item's metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets where the item came from, when the provider could tell.</summary>
    public ProjectItemOrigin Origin { get; init; }

    /// <summary>Returns the item type and its include.</summary>
    /// <returns>Something like <c>Compile: Program.cs</c>.</returns>
    public override string ToString() => $"{ItemType}: {Include}";
}
