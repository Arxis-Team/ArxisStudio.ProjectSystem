namespace ArxisStudio.ProjectSystem;

/// <summary>What happened to a file, as a file system reports it.</summary>
/// <remarks>
/// The distinction a bare path cannot carry. A file that changed needs its readers told; a file
/// that appeared or went away may change what a project includes; a rename does both and also moves
/// whatever had the old path open. <see cref="SolutionSnapshot.Classify"/> answers differently for
/// each, which is why the kind travels with the path.
/// </remarks>
public enum FileChangeKind
{
    /// <summary>The file's contents changed, or its timestamp or size did.</summary>
    Changed = 0,

    /// <summary>The file appeared.</summary>
    Created = 1,

    /// <summary>The file went away.</summary>
    Deleted = 2,

    /// <summary>
    /// The file was renamed: <see cref="FileChange.OldPath"/> is where it was, <see cref="FileChange.Path"/>
    /// where it is.
    /// </summary>
    Renamed = 3,

    /// <summary>
    /// The watcher lost changes — its buffer overflowed — and what changed is unknowable. The only
    /// answer that cannot miss one is to look at everything again.
    /// </summary>
    Overflow = 4,
}
