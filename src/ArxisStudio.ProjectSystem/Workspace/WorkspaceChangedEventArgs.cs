using System;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Reports that a workspace published a new snapshot.
/// </summary>
/// <remarks>
/// Raised after publication, so a handler that reads
/// <see cref="ProjectWorkspace.CurrentSnapshot"/> sees <see cref="Snapshot"/> there.
/// </remarks>
public sealed class WorkspaceChangedEventArgs : EventArgs
{
    internal WorkspaceChangedEventArgs(SolutionSnapshot snapshot, SolutionSnapshot? previous)
    {
        Snapshot = snapshot;
        Previous = previous;
    }

    /// <summary>Gets the snapshot that was just published.</summary>
    public SolutionSnapshot Snapshot { get; }

    /// <summary>
    /// Gets the snapshot this one replaced, or <see langword="null"/> when it is the first.
    /// </summary>
    /// <remarks>
    /// There is no <c>Kind</c> enum beside this, because <c>Previous is null</c> already says
    /// whether this was an initial load or a refresh, and two ways of asking one question is one
    /// too many.
    /// </remarks>
    public SolutionSnapshot? Previous { get; }

    /// <summary>Gets the version the snapshot was published at.</summary>
    public WorkspaceVersion Version => Snapshot.Version;
}
