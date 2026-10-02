using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Watches everything a solution's projects are made of, and says what happened to each path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectFileWatcher"/> watches what evaluations read, and for those any change means the
/// same thing. A host that shows files needs more — a saved form is not a new one, and a moved one is
/// neither — so this one reports each change with its kind (<see cref="FileChange"/>). Feed them to
/// <see cref="FileChangeCoalescer.ForChanges"/> and classify each batch with
/// <see cref="SolutionSnapshot.Classify"/>
/// (<see href="../../docs/adr/0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md">ADR 0025</see>).
/// It watches what <see cref="ProjectFileWatcher"/> would as well, so a host needs one or the other.
/// </para>
/// <para>
/// <b>Each project's directory is watched with everything below it, in one watch</b>, so that a folder
/// created in it is heard together with its contents. A build's own writes come with it;
/// <see cref="SolutionSnapshot.Classify"/> leaves them out by <see cref="ProjectSnapshot.BuildDirectories"/>,
/// which is why this decides nothing itself. Files outside every project directory — the solution,
/// imports above the projects, files linked in — are watched through their own directories.
/// </para>
/// <para>
/// <b>A move is reported as the operating system reports it.</b> A rename in place is a rename; a file
/// moved to another folder is a deletion and a creation, even inside one watch, and the coalescer joins
/// the two into the move they were. A rename the operating system saw only half of — a file moved in
/// from a directory nobody watches, or out to one — is the half it saw, as
/// <see cref="FileChangeKind.Created"/> or <see cref="FileChangeKind.Deleted"/>.
/// </para>
/// <para>
/// <b>Lost changes are said, not guessed.</b> When the operating system's notification buffer
/// overflows, or a watch fails, the changes in between are unknowable: <see cref="FileChange.Overflow"/>
/// is reported, and <see cref="WorkspaceChangeSet.RequiresRescan"/> tells the host to look at everything
/// again.
/// </para>
/// </remarks>
public sealed class ProjectSourceWatcher : IDisposable
{
    /// <summary>64 KiB, as <see cref="ProjectFileWatcher"/> — room is far cheaper than a rescan.</summary>
    private const int BufferSize = 64 * 1024;

    private readonly Action<FileChange> _onChange;
    private readonly Lock _sync = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private bool _disposed;

    /// <summary>Creates a watcher.</summary>
    /// <param name="onChange">
    /// Called with each change, on an operating-system thread, possibly several at once and possibly
    /// for files nothing cares about. <see cref="FileChangeCoalescer.Add(FileChange)"/> is the intended
    /// destination and is safe to call from anywhere. An exception it throws is swallowed, because the
    /// alternative on that thread is ending the process.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="onChange"/> is <see langword="null"/>.</exception>
    public ProjectSourceWatcher(Action<FileChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        _onChange = onChange;
    }

    /// <summary>
    /// Watches what a snapshot's projects are made of, and stops watching whatever was watched before.
    /// </summary>
    /// <remarks>
    /// Replaces rather than adds: a refresh produces a new snapshot, and a project that went away
    /// should stop being heard. Call it again after every publication.
    /// </remarks>
    /// <param name="snapshot">The solution to watch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This watcher has been disposed.</exception>
    public void Watch(SolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(snapshot, static directory => Directory.Exists(directory.Value));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopAll();

            foreach (WatchedDirectory directory in plan)
            {
                Start(directory);
            }
        }
    }

    /// <summary>Stops watching everything.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            StopAll();
        }
    }

    private void Start(WatchedDirectory directory)
    {
        FileSystemWatcher? watcher = null;

        try
        {
            watcher = new FileSystemWatcher(directory.Directory.Value)
            {
                IncludeSubdirectories = directory.IncludeSubdirectories,
                InternalBufferSize = BufferSize,

                // Every write moves the last-write time, so size would only report each write twice.
                // Directory names are here because a folder renamed or deleted is one event and
                // nothing about what it held.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Changed += (_, e) => Report(e.FullPath, FileChangeKind.Changed);
            watcher.Created += (_, e) => Report(e.FullPath, FileChangeKind.Created);
            watcher.Deleted += (_, e) => Report(e.FullPath, FileChangeKind.Deleted);
            watcher.Renamed += OnRenamed;
            watcher.Error += (_, _) => Report(FileChange.Overflow);

            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gone between the plan and the watch, or not readable by this process. The rest is still
            // watched; a directory that comes back is picked up by the next Watch.
            watcher?.Dispose();
        }
    }

    private void StopAll()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>
    /// A rename, or the half of one the operating system saw.
    /// </summary>
    /// <remarks>
    /// The names, not the full paths, say whether an end is missing: a missing name is an empty
    /// string, and the full path built from it is the watched directory itself — which would read
    /// as that whole directory being renamed.
    /// </remarks>
    /// <returns>The change, or <see langword="null"/> when neither end is a usable path.</returns>
    internal static FileChange? FromRename(RenamedEventArgs e)
    {
        CanonicalPath from = string.IsNullOrEmpty(e.OldName) ? CanonicalPath.None : Canonical(e.OldFullPath);
        CanonicalPath to = string.IsNullOrEmpty(e.Name) ? CanonicalPath.None : Canonical(e.FullPath);

        if (!from.IsEmpty && !to.IsEmpty)
        {
            return FileChange.Renamed(from, to);
        }

        if (!to.IsEmpty)
        {
            return new FileChange(to, FileChangeKind.Created);
        }

        return from.IsEmpty ? null : new FileChange(from, FileChangeKind.Deleted);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (FromRename(e) is { } change)
        {
            Report(change);
        }
    }

    private static CanonicalPath Canonical(string? fullPath) =>
        CanonicalPath.TryCreate(fullPath, out CanonicalPath path) ? path : CanonicalPath.None;

    private void Report(string? fullPath, FileChangeKind kind)
    {
        if (Canonical(fullPath) is { IsEmpty: false } path)
        {
            Report(new FileChange(path, kind));
        }
    }

    private void Report(FileChange change)
    {
        try
        {
            _onChange(change);
        }
#pragma warning disable CA1031 // Raised on an operating-system thread, where an escaping exception
        catch (Exception)      // ends the process. Isolated, as every other callback here is.
#pragma warning restore CA1031
        {
        }
    }
}
