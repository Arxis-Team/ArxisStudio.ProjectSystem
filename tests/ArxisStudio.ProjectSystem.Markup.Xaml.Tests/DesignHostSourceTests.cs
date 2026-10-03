using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// A host inside an IDE reads the project and builds it through the IDE's service, and saves through the
/// IDE's file service — the two seams of ADR 0032.
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostSourceTests
{
    [AvaloniaFact]
    public async Task SaveAsync_AWriterGiven_WritesThroughItAndNotInPlace()
    {
        var writer = new RecordingWriter();

        await using DesignStand stand = await DesignStand.StartAsync(
            null,
            TestContext.Current.CancellationToken,
            options => options with { Writer = writer });

        CanonicalPath file = stand.Fixtures.Document("FixtureControl.axaml");
        string before = await File.ReadAllTextAsync(file.Value, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "saved"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        await stand.Host.SaveAsync(document, TestContext.Current.CancellationToken);

        (CanonicalPath written, SourceText text) = Assert.Single(writer.Writes);

        Assert.Equal(file, written);
        Assert.Equal(document.Document.SourceText.ToString(), text.ToString());
        Assert.False(document.IsDirty);

        // The writer is the IDE's file service: the host did not write the file behind its back.
        Assert.Equal(before, await File.ReadAllTextAsync(file.Value, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SaveAsync_AWriterThatThrows_LeavesTheDocumentUnsaved()
    {
        var writer = new RecordingWriter { Fails = true };

        await using DesignStand stand = await DesignStand.StartAsync(
            null,
            TestContext.Current.CancellationToken,
            options => options with { Writer = writer });

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "unsaved"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(
            async () => await stand.Host.SaveAsync(document, TestContext.Current.CancellationToken));

        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task StartAsync_OverTheOwnersSource_BuildsThroughItWithTheBuildProperties()
    {
        RecordingSource? recording = null;

        await using DesignStand stand = await DesignStand.StartAsync(
            static (fixtures, _) => fixtures.Write("Code.cs", "class Code { }"),
            TestContext.Current.CancellationToken,
            source: bench => recording = new RecordingSource(ProjectDesignSource.From(bench.Workspace)));

        ProjectOperationRequest build = Assert.Single(recording!.Executed);

        Assert.Equal(ProjectOperationKind.Build, build.Kind);
        Assert.True(build.GlobalProperties.TryGetValue("OutputPath", out string? output));
        Assert.Equal("bin/ArxisStudio/", output);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task StartAsync_ASourceWithNoSnapshotYet_Throws()
    {
        using var fixtures = new DesignFixtures();
        await using var bench = new DesignBench(fixtures);

        // Nothing loaded: a generation is of a snapshot's projects, and there is none.
        await using var host = new ProjectDesignHost(ProjectDesignSource.From(bench.Workspace), bench.Options());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task From_AHandlerRemoved_IsNoLongerToldOfTheWorkspacesSnapshots()
    {
        using var fixtures = new DesignFixtures();
        await using var bench = new DesignBench(fixtures);

        IProjectDesignSource source = ProjectDesignSource.From(bench.Workspace);
        int raised = 0;

        void Count(object? sender, EventArgs e)
        {
            Assert.Same(source, sender);
            Interlocked.Increment(ref raised);
        }

        source.SnapshotChanged += Count;

        await bench.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.Same(bench.Workspace.CurrentSnapshot, source.Snapshot);

        source.SnapshotChanged -= Count;

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    /// <summary>A writer that records what it was given, writes nothing, and fails when told to.</summary>
    private sealed class RecordingWriter : IProjectDesignWriter
    {
        private readonly Lock _sync = new();
        private readonly List<(CanonicalPath File, SourceText Text)> _writes = [];

        public bool Fails { get; init; }

        public IReadOnlyList<(CanonicalPath File, SourceText Text)> Writes
        {
            get
            {
                lock (_sync)
                {
                    return [.. _writes];
                }
            }
        }

        public ValueTask WriteAsync(CanonicalPath file, SourceText text, CancellationToken cancellationToken)
        {
            if (Fails)
            {
                throw new IOException("The file service refused the write.");
            }

            lock (_sync)
            {
                _writes.Add((file, text));
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A source that passes everything on and records the operations it ran.</summary>
    private sealed class RecordingSource(IProjectDesignSource inner) : IProjectDesignSource
    {
        private readonly Lock _sync = new();
        private readonly List<ProjectOperationRequest> _executed = [];

        public IReadOnlyList<ProjectOperationRequest> Executed
        {
            get
            {
                lock (_sync)
                {
                    return [.. _executed];
                }
            }
        }

        public SolutionSnapshot? Snapshot => inner.Snapshot;

        public event EventHandler? SnapshotChanged
        {
            add => inner.SnapshotChanged += value;
            remove => inner.SnapshotChanged -= value;
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken) => inner.RefreshAsync(cancellationToken);

        public ValueTask<ProjectOperationResult> ExecuteAsync(
            ProjectOperationRequest request,
            IProgress<ProjectOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _executed.Add(request);
            }

            return inner.ExecuteAsync(request, progress, cancellationToken);
        }
    }
}
