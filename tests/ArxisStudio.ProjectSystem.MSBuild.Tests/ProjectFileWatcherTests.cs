using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// The watcher against a real file system.
/// </summary>
/// <remarks>
/// <para>
/// Everything decidable without a disk is decided in <see cref="FileWatchPlanTests"/>. What is left
/// is the wiring, and wiring to an operating system can only be shown by using one.
/// </para>
/// <para>
/// These wait on a <see cref="TaskCompletionSource"/> that the notification completes, which is the
/// same coordination the workspace tests use — the only difference is that the operating system
/// resumes it rather than the test. Nothing sleeps, nothing polls, and nothing passes by being fast
/// enough. A notification that never arrives hangs until the test framework's own token cancels it,
/// which is a failure rather than a flake.
/// </para>
/// </remarks>
public sealed class ProjectFileWatcherTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "arxis-watch-" + Guid.NewGuid().ToString("N"));

    public ProjectFileWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher on the way down can hold a handle for a moment. A temporary directory left
            // behind is the operating system's problem, not a reason to fail a passing test.
        }
    }

    [Fact]
    public async Task AWatchedFileBeingEdited_IsReported()
    {
        string file = System.IO.Path.Combine(_root, "Directory.Build.props");
        await File.WriteAllTextAsync(file, "<Project />", TestContext.Current.CancellationToken);

        var reported = new TaskCompletionSource<CanonicalPath>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new ProjectFileWatcher(path => reported.TrySetResult(path));
        watcher.Watch([CanonicalPath.Create(file)]);

        await File.WriteAllTextAsync(file, "<Project><!-- edited --></Project>", TestContext.Current.CancellationToken);

        Assert.Equal(CanonicalPath.Create(file), await reported.Task.WaitAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The case the ancestor rule exists for: the file and its directory are both missing, and the
    /// thing worth hearing about is them appearing.
    /// </summary>
    [Fact]
    public async Task AFileAppearingInADirectoryThatDidNotExist_IsReported()
    {
        string assets = System.IO.Path.Combine(_root, "obj", "project.assets.json");

        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new ProjectFileWatcher(path =>
        {
            if (path == CanonicalPath.Create(assets))
            {
                reported.TrySetResult();
            }
        });

        watcher.Watch([CanonicalPath.Create(assets)]);

        Directory.CreateDirectory(System.IO.Path.Combine(_root, "obj"));
        await File.WriteAllTextAsync(assets, "{}", TestContext.Current.CancellationToken);

        await reported.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The set comes from a snapshot and a refresh produces a new one, so a project that stopped
    /// importing something must stop hearing about it.
    /// </summary>
    [Fact]
    public async Task Watching_ReplacesWhatWasWatchedBefore()
    {
        string first = System.IO.Path.Combine(_root, "first", "Directory.Build.props");
        string second = System.IO.Path.Combine(_root, "second", "Directory.Build.props");

        Directory.CreateDirectory(System.IO.Path.Combine(_root, "first"));
        Directory.CreateDirectory(System.IO.Path.Combine(_root, "second"));

        await File.WriteAllTextAsync(first, "<Project />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "<Project />", TestContext.Current.CancellationToken);

        var seen = new List<CanonicalPath>();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new ProjectFileWatcher(path =>
        {
            lock (seen)
            {
                seen.Add(path);
            }

            if (path == CanonicalPath.Create(second))
            {
                reported.TrySetResult();
            }
        });

        watcher.Watch([CanonicalPath.Create(first)]);
        watcher.Watch([CanonicalPath.Create(second)]);

        // The negative assertion below is exact, and not because these two notifications are
        // ordered -- they come from different watchers and nothing orders those. It is exact
        // because Watch disposes the previous watchers before it returns, so by the time this line
        // runs there is no handle open on the first directory and no notification can be produced
        // for it. Awaiting the second is only what stops the test finishing early.
        await File.WriteAllTextAsync(first, "<Project><!-- edited --></Project>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "<Project><!-- edited --></Project>", TestContext.Current.CancellationToken);

        await reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        lock (seen)
        {
            Assert.DoesNotContain(CanonicalPath.Create(first), seen);
        }
    }

    [Fact]
    public async Task AfterDisposal_NothingIsReported()
    {
        string file = System.IO.Path.Combine(_root, "Directory.Build.props");
        await File.WriteAllTextAsync(file, "<Project />", TestContext.Current.CancellationToken);

        int reports = 0;

        var watcher = new ProjectFileWatcher(_ => Interlocked.Increment(ref reports));
        watcher.Watch([CanonicalPath.Create(file)]);
        watcher.Dispose();

        await File.WriteAllTextAsync(file, "<Project><!-- edited --></Project>", TestContext.Current.CancellationToken);

        Assert.Throws<ObjectDisposedException>(() => watcher.Watch([CanonicalPath.Create(file)]));

        // Disposal stops the watcher synchronously, so a notification queued before it could still
        // be in flight -- but a change made afterwards has nothing left to observe it.
        watcher.Dispose();
    }

    [Fact]
    public void ANullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectFileWatcher(null!));

        using var watcher = new ProjectFileWatcher(static _ => { });

        Assert.Throws<ArgumentNullException>(() => watcher.Watch(null!));
    }

    /// <summary>
    /// A directory that is not there and has no ancestor to fall back on. The caller asked to be
    /// told about changes, not to be told the disk moved.
    /// </summary>
    [Fact]
    public void WatchingSomewhereUnreachable_DoesNotThrow()
    {
        using var watcher = new ProjectFileWatcher(static _ => { });

        watcher.Watch([CanonicalPath.Create(System.IO.Path.Combine(_root, "gone", "deeper", "App.csproj"))]);
        watcher.Watch([]);
    }
}
