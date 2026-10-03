using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>A design host started over the bench, and the fixtures under both, disposed in the right order.</summary>
internal sealed class DesignStand : IAsyncDisposable
{
    private DesignStand(DesignFixtures fixtures, DesignBench bench, ProjectDesignHost host)
    {
        Fixtures = fixtures;
        Bench = bench;
        Host = host;
    }

    /// <summary>Gets how long a test waits for something the host was asked to do before it fails.</summary>
    internal static TimeSpan Patience { get; } = TimeSpan.FromSeconds(30);

    internal DesignFixtures Fixtures { get; }

    internal DesignBench Bench { get; }

    internal ProjectDesignHost Host { get; }

    /// <summary>Lays the fixtures out, loads the workspace, and starts a host over it.</summary>
    /// <param name="arrange">Changes the project before the workspace is loaded, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <param name="options">Changes the host's options, or <see langword="null"/>.</param>
    /// <param name="source">
    /// Makes the source the host reads and builds through, or <see langword="null"/> for the bench's
    /// workspace itself.
    /// </param>
    /// <returns>The stand.</returns>
    internal static async Task<DesignStand> StartAsync(
        Action<DesignFixtures, DesignBench>? arrange,
        CancellationToken cancellationToken,
        Func<ProjectDesignHostOptions, ProjectDesignHostOptions>? options = null,
        Func<DesignBench, IProjectDesignSource>? source = null)
    {
        var fixtures = new DesignFixtures();
        var bench = new DesignBench(fixtures);

        arrange?.Invoke(fixtures, bench);

        await bench.LoadAsync(cancellationToken);

        ProjectDesignHostOptions chosen = bench.Options();
        ProjectDesignHostOptions given = options?.Invoke(chosen) ?? chosen;
        ProjectDesignHost host = source is null
            ? new ProjectDesignHost(bench.Workspace, given)
            : new ProjectDesignHost(source(bench), given);

        await host.StartAsync(cancellationToken);

        return new DesignStand(fixtures, bench, host);
    }

    /// <summary>Opens one of the project's documents, which has to show.</summary>
    internal async Task<XamlLiveDocument> OpenAsync(string name, CancellationToken cancellationToken)
    {
        XamlLiveDocument document = await Host.OpenDocumentAsync(Fixtures.Document(name), cancellationToken: cancellationToken);

        Xunit.Assert.True(
            document.State == XamlLiveDocumentState.Live,
            $"{name} opened {document.State}: " + string.Join(" | ", document.Diagnostics.Select(static d => $"{d.Code} {d.Message}")));

        return document;
    }

    /// <summary>The name of the generation a document's root was built from, without holding the root.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? GenerationOf(XamlLiveDocument document) =>
        document.Session?.RootObject is { } root ? AssemblyLoadContext.GetLoadContext(root.GetType().Assembly)?.Name : null;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Bench.DisposeAsync();

        Fixtures.Dispose();
    }
}

/// <summary>Records an event as it is raised, and lets a test wait for the next occurrences.</summary>
/// <typeparam name="T">What the event carries.</typeparam>
internal sealed class EventProbe<T>
{
    private readonly Lock _sync = new();
    private readonly List<T> _seen = [];
    private readonly List<(int Count, TaskCompletionSource Waiter)> _waiters = [];

    /// <summary>Gets what was raised, in order.</summary>
    internal IReadOnlyList<T> Seen
    {
        get
        {
            lock (_sync)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>Records one occurrence; subscribe this.</summary>
    internal void Record(object? sender, T arguments)
    {
        List<TaskCompletionSource> done = [];

        lock (_sync)
        {
            _seen.Add(arguments);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Count <= _seen.Count)
                {
                    done.Add(_waiters[i].Waiter);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (TaskCompletionSource waiter in done)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>Completes once the event was raised at least this many times in all, failing after the stand's patience.</summary>
    internal Task WhenCountAsync(int count)
    {
        lock (_sync)
        {
            if (_seen.Count >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _waiters.Add((count, waiter));

            return waiter.Task.WaitAsync(DesignStand.Patience);
        }
    }
}

/// <summary>A part of a designer that a test can stop inside its letting go.</summary>
internal sealed class ParkingParticipant : IProjectDesignParticipant
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _letGo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets whether letting go parks until <see cref="LetGo"/>.</summary>
    internal bool Parks { get; set; }

    /// <summary>Gets what the participant saw, in order: "release", "restore".</summary>
    internal List<string> Calls { get; } = [];

    /// <summary>Gets or sets what to observe when asked to let go and to take up.</summary>
    internal Func<string>? Observe { get; set; }

    /// <summary>Completes when the participant is asked to let go.</summary>
    internal Task Arrived => _arrived.Task.WaitAsync(DesignStand.Patience);

    internal void LetGo() => _letGo.TrySetResult();

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken)
    {
        Calls.Add("release" + (Observe is { } observe ? ":" + observe() : string.Empty));

        _arrived.TrySetResult();

        if (Parks)
        {
            await _letGo.Task.WaitAsync(cancellationToken);
        }
    }

    public ValueTask RestoreAsync(CancellationToken cancellationToken)
    {
        Calls.Add("restore" + (Observe is { } observe ? ":" + observe() : string.Empty));

        return ValueTask.CompletedTask;
    }
}
