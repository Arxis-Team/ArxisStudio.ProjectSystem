using System;
using System.Collections.Immutable;
using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>What a design build did.</summary>
public sealed class ProjectDesignBuildResult
{
    internal ProjectDesignBuildResult(
        ProjectOperationStatus status,
        ImmutableArray<ProjectIdentity> projects,
        ImmutableArray<ProjectDiagnostic> diagnostics,
        string reason,
        bool restored,
        bool typesChanged,
        TimeSpan duration)
    {
        Status = status;
        Projects = projects;
        Diagnostics = diagnostics;
        Reason = reason;
        Restored = restored;
        TypesChanged = typesChanged;
        Duration = duration;
    }

    /// <summary>Gets whether every project built.</summary>
    public ProjectOperationStatus Status { get; }

    /// <summary>
    /// Gets the projects built: the top ones of what changed, each building what it references. Empty
    /// when nothing of the design set was affected.
    /// </summary>
    public ImmutableArray<ProjectIdentity> Projects { get; }

    /// <summary>Gets what the restores and builds reported.</summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>Gets why the build ran, as the caller or the host said it.</summary>
    public string Reason { get; }

    /// <summary>Gets a value indicating whether a restore ran first.</summary>
    public bool Restored { get; }

    /// <summary>
    /// Gets a value indicating whether the build rewrote what the live generation was loaded from, so
    /// that the types are to be swapped.
    /// </summary>
    public bool TypesChanged { get; }

    /// <summary>Gets how long the restores and builds took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Returns the status, the projects and why.</summary>
    /// <returns>Something like <c>Succeeded: 1 project (code changed), types changed</c>.</returns>
    public override string ToString() =>
        $"{Status}: {Projects.Length} project{(Projects.Length == 1 ? string.Empty : "s")} ({Reason})"
        + (TypesChanged ? ", types changed" : string.Empty);
}

/// <summary>What replacing a generation did, phase by phase.</summary>
/// <remarks>
/// The phases are the order <see cref="ProjectDesignHost"/> replaces a generation in: the designer
/// lets go (participants, documents), what the host holds goes (population, environments), the
/// generation is reclaimed, and the successor is built and shown.
/// </remarks>
public sealed class ProjectDesignSwapReport
{
    internal ProjectDesignSwapReport(
        string reason,
        string generation,
        string? successor,
        bool reclaimed,
        TimeSpan release,
        TimeSpan teardown,
        TimeSpan reclaim,
        TimeSpan rebuild)
    {
        Reason = reason;
        Generation = generation;
        Successor = successor;
        Reclaimed = reclaimed;
        Release = release;
        Teardown = teardown;
        Reclaim = reclaim;
        Rebuild = rebuild;
    }

    /// <summary>Gets why the generation was replaced.</summary>
    public string Reason { get; }

    /// <summary>Gets the name of the generation replaced.</summary>
    public string Generation { get; }

    /// <summary>Gets the name of the successor, or <see langword="null"/> when there is none.</summary>
    public string? Successor { get; }

    /// <summary>
    /// Gets a value indicating whether the generation was proven gone. When it was not, no successor
    /// was created and the host requires a restart.
    /// </summary>
    public bool Reclaimed { get; }

    /// <summary>Gets how long the participants and the documents took to let go.</summary>
    public TimeSpan Release { get; }

    /// <summary>Gets how long the host took to let go of the population and the environments.</summary>
    public TimeSpan Teardown { get; }

    /// <summary>Gets how long proving the generation gone took.</summary>
    public TimeSpan Reclaim { get; }

    /// <summary>Gets how long the successor took to be loaded, populated and shown.</summary>
    public TimeSpan Rebuild { get; }

    /// <summary>Returns the outcome and the phases.</summary>
    /// <returns>Something like <c>reclaimed: release 40 ms, teardown 2 ms, reclaim 210 ms, rebuild 90 ms</c>.</returns>
    public override string ToString() =>
        $"{(Reclaimed ? "reclaimed" : "still held")}: release {Release.TotalMilliseconds:F0} ms, "
        + $"teardown {Teardown.TotalMilliseconds:F0} ms, reclaim {Reclaim.TotalMilliseconds:F0} ms, "
        + $"rebuild {Rebuild.TotalMilliseconds:F0} ms";
}

/// <summary>Raised when a design build finished.</summary>
public sealed class ProjectDesignBuildCompletedEventArgs : EventArgs
{
    internal ProjectDesignBuildCompletedEventArgs(ProjectDesignBuildResult result) => Result = result;

    /// <summary>Gets what the build did.</summary>
    public ProjectDesignBuildResult Result { get; }
}

/// <summary>Raised when a generation was replaced, or found held.</summary>
public sealed class ProjectDesignSwapCompletedEventArgs : EventArgs
{
    internal ProjectDesignSwapCompletedEventArgs(ProjectDesignSwapReport report) => Report = report;

    /// <summary>Gets what the swap did, phase by phase.</summary>
    public ProjectDesignSwapReport Report { get; }
}

/// <summary>Why only a new process can show the project's types as they are now.</summary>
public enum ProjectDesignRestartReason
{
    /// <summary>The generation was let go of and is still held: something in the process keeps it.</summary>
    GenerationStillHeld = 0,

    /// <summary>
    /// A package the generation loaded changed to another file — another version — and a package loaded
    /// into the default context stays for the life of the process.
    /// </summary>
    PackagesChanged = 1,
}

/// <summary>Raised when only a new process can show the project's types as they are now.</summary>
public sealed class ProjectDesignRestartEventArgs : EventArgs
{
    internal ProjectDesignRestartEventArgs(ProjectDesignRestartReason reason, string message)
    {
        Reason = reason;
        Message = message;
    }

    /// <summary>Gets why.</summary>
    public ProjectDesignRestartReason Reason { get; }

    /// <summary>Gets why, for a person.</summary>
    public string Message { get; }
}

/// <summary>Raised when a document's file changed on disk under edits the document has and the file does not.</summary>
/// <remarks>
/// Nothing was applied: the person decides — <see cref="XamlExternalTextPolicy.TakeTheirs"/> or
/// <see cref="XamlExternalTextPolicy.KeepMine"/> through <see cref="XamlLiveDocument.AcceptExternalTextAsync"/>.
/// </remarks>
public sealed class ProjectDesignConflictEventArgs : EventArgs
{
    internal ProjectDesignConflictEventArgs(XamlLiveDocument document, CanonicalPath file, string diskText)
    {
        Document = document;
        File = file;
        DiskText = diskText;
    }

    /// <summary>Gets the document.</summary>
    public XamlLiveDocument Document { get; }

    /// <summary>Gets its file.</summary>
    public CanonicalPath File { get; }

    /// <summary>Gets what the file says now.</summary>
    public string DiskText { get; }
}

/// <summary>Raised when an open document's file was renamed, moved or deleted by somebody else.</summary>
public sealed class ProjectDesignDocumentEventArgs : EventArgs
{
    internal ProjectDesignDocumentEventArgs(XamlLiveDocument document, CanonicalPath file, CanonicalPath previous)
    {
        Document = document;
        File = file;
        Previous = previous;
    }

    /// <summary>Gets the document.</summary>
    public XamlLiveDocument Document { get; }

    /// <summary>Gets where its file is now — the same as <see cref="Previous"/> for one deleted.</summary>
    public CanonicalPath File { get; }

    /// <summary>Gets where its file was.</summary>
    public CanonicalPath Previous { get; }
}

/// <summary>Raised once the host has dealt with a batch of file changes.</summary>
public sealed class ProjectDesignChangesEventArgs : EventArgs
{
    internal ProjectDesignChangesEventArgs(ImmutableArray<FileChange> batch, WorkspaceChangeSet changes)
    {
        Batch = batch;
        Changes = changes;
    }

    /// <summary>Gets the changes, as the coalescer delivered them.</summary>
    public ImmutableArray<FileChange> Batch { get; }

    /// <summary>Gets what they meant to the snapshot they arrived against.</summary>
    public WorkspaceChangeSet Changes { get; }
}

/// <summary>Raised when something the host ran on its own failed in a way nothing else reports.</summary>
public sealed class ProjectDesignFailureEventArgs : EventArgs
{
    internal ProjectDesignFailureEventArgs(string operation, Exception exception)
    {
        Operation = operation;
        Exception = exception;
    }

    /// <summary>Gets what the host was doing.</summary>
    public string Operation { get; }

    /// <summary>Gets what went wrong.</summary>
    public Exception Exception { get; }
}
