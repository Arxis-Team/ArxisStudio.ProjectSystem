namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A change to one file, as a watcher or a host's own file observation reports it.
/// </summary>
/// <remarks>
/// <para>
/// A core type rather than a watcher's event type, so a host that already observes files — an IDE
/// almost always does — reports in it without taking this library's watcher
/// (<see href="../../docs/adr/0016-watching-belongs-with-the-provider.md">ADR 0016</see>).
/// </para>
/// <para>
/// <see cref="FileChangeCoalescer"/> reduces a burst of them to what is true at its end, path by path:
/// a file created and deleted inside one batch never happened, and a file replaced through a
/// temporary file and two renames — the way JetBrains Rider and every atomic writer save — changed.
/// </para>
/// </remarks>
/// <param name="Path">The file. For <see cref="FileChangeKind.Renamed"/>, where it is now; empty for <see cref="FileChangeKind.Overflow"/>.</param>
/// <param name="Kind">What happened to it.</param>
public readonly record struct FileChange(CanonicalPath Path, FileChangeKind Kind)
{
    /// <summary>
    /// Gets where a renamed file was; empty for every other kind.
    /// </summary>
    public CanonicalPath OldPath { get; init; }

    /// <summary>Gets the change that says the watcher lost changes.</summary>
    public static FileChange Overflow { get; } = new(CanonicalPath.None, FileChangeKind.Overflow);

    /// <summary>Creates the change for a file renamed from one path to another.</summary>
    /// <param name="oldPath">Where the file was.</param>
    /// <param name="newPath">Where it is.</param>
    /// <returns>The change.</returns>
    public static FileChange Renamed(CanonicalPath oldPath, CanonicalPath newPath) =>
        new(newPath, FileChangeKind.Renamed) { OldPath = oldPath };

    /// <summary>Returns the kind and the path, and where a renamed file was.</summary>
    /// <returns>Something like <c>Renamed: C:\App\Old.axaml -> C:\App\New.axaml</c>.</returns>
    public override string ToString() => Kind switch
    {
        FileChangeKind.Renamed => $"Renamed: {OldPath} -> {Path}",
        FileChangeKind.Overflow => "Overflow",
        _ => $"{Kind}: {Path}",
    };
}
