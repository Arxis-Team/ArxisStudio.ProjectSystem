using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Watches the files a snapshot was built from, and says when one of them changes.
/// </summary>
/// <remarks>
/// <para>
/// Hand it a snapshot's <see cref="ProjectSnapshot.EvaluationInputs"/> and it reports changes;
/// feed those to a <see cref="FileChangeCoalescer"/> and then to
/// <see cref="SolutionSnapshot.Invalidate"/> to find out whether they matter. It decides nothing
/// itself, which is what keeps it small.
/// </para>
/// <para>
/// A host that already observes files — an IDE usually does — should feed its own notifications to
/// the coalescer instead and skip this entirely. It exists so that a host without one does not have
/// to rediscover the three things below.
/// </para>
/// <para>
/// <b>Directories are watched, not files.</b> A watcher bound to a single file goes deaf the moment
/// that file is replaced rather than edited, which is what most editors and every tool that writes
/// atomically actually do, and it can never see the file appear in the first place.
/// </para>
/// <para>
/// <b>A missing directory is watched through its nearest existing ancestor</b>, with subdirectories
/// included. An unrestored project names <c>obj/project.assets.json</c> and has no <c>obj</c>, so
/// without this the one change that most needs noticing — a restore finishing — would be the one
/// change that never arrived. The cost is that build output in that tree is reported too; the
/// coalescer absorbs the volume and <c>Invalidate</c> discards it. Once the directory exists, the
/// next <see cref="Watch"/> narrows to it.
/// </para>
/// <para>
/// <b>An overflow reports everything.</b> The operating system's notification buffer is finite and a
/// branch switch will exhaust it. When that happens the changes are gone and unknowable, so every
/// watched path is reported as changed — the only honest answer, and the one that produces a refresh
/// rather than a silent, permanent staleness.
/// </para>
/// </remarks>
public sealed class ProjectFileWatcher : IDisposable
{
    /// <summary>
    /// 64 KiB, the maximum the operating system accepts for a network path and eight times the
    /// default. Buying room here is much cheaper than the overflow it avoids, which costs a full
    /// reload.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    private readonly Action<CanonicalPath> _onChanged;
    private readonly Lock _sync = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private ImmutableArray<CanonicalPath> _watched = [];
    private bool _disposed;

    /// <summary>Creates a watcher.</summary>
    /// <param name="onChanged">
    /// Called with each changed path, on an operating-system thread, possibly several at once and
    /// possibly for files nothing cares about. <see cref="FileChangeCoalescer.Add"/> is the intended
    /// destination and is safe to call from anywhere. An exception it throws is swallowed, because
    /// the alternative on that thread is ending the process.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="onChanged"/> is <see langword="null"/>.</exception>
    public ProjectFileWatcher(Action<CanonicalPath> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        _onChanged = onChanged;
    }

    /// <summary>
    /// Watches these files, and stops watching whatever was being watched before.
    /// </summary>
    /// <remarks>
    /// Replaces rather than adds, because the set to watch comes from a snapshot and a refresh
    /// produces a new one: a project that stopped importing something should stop hearing about it.
    /// Call it again after every publication, with the new snapshot's inputs.
    /// </remarks>
    /// <param name="paths">The files to watch. Empty paths are ignored; an empty set watches nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This watcher has been disposed.</exception>
    public void Watch(IEnumerable<CanonicalPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ImmutableArray<CanonicalPath> watched = [.. paths];
        ImmutableArray<WatchedDirectory> plan = FileWatchPlan.For(watched, Exists);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopAll();

            _watched = watched;

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
            _watched = [];

            StopAll();
        }
    }

    private static bool Exists(CanonicalPath directory) => Directory.Exists(directory.Value);

    private void Start(WatchedDirectory directory)
    {
        FileSystemWatcher? watcher = null;

        try
        {
            watcher = new FileSystemWatcher(directory.Directory.Value)
            {
                IncludeSubdirectories = directory.IncludeSubdirectories,
                InternalBufferSize = BufferSize,

                // Size and CreationTime are deliberately absent. A build touches both on files
                // nobody asked about, and everything that changes what a project says also changes
                // its last-write time or its name.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory went away between the plan and the watch, or is one this process may not
            // read. Neither is worth failing a load over: the caller asked to be told about changes,
            // not to be told the disk moved. The remaining directories are still watched.
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

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Report(e.FullPath);

    /// <summary>
    /// Both ends of a rename, because either can be the interesting one: a file renamed away has
    /// gone, and a file renamed into place has arrived — which is how an atomic save appears.
    /// </summary>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Report(e.OldFullPath);
        Report(e.FullPath);
    }

    /// <summary>
    /// The buffer overflowed and the changes are unknowable. Reporting every watched path is the
    /// only answer that cannot silently miss one.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        ImmutableArray<CanonicalPath> watched;

        lock (_sync)
        {
            watched = _watched;
        }

        foreach (CanonicalPath path in watched)
        {
            Report(path);
        }
    }

    private void Report(string? fullPath)
    {
        if (!CanonicalPath.TryCreate(fullPath, out CanonicalPath path))
        {
            return;
        }

        Report(path);
    }

    private void Report(CanonicalPath path)
    {
        try
        {
            _onChanged(path);
        }
#pragma warning disable CA1031 // Raised on an operating-system thread, where an escaping exception
        catch (Exception)      // ends the process. Isolated, as every other callback here is.
#pragma warning restore CA1031
        {
        }
    }
}
