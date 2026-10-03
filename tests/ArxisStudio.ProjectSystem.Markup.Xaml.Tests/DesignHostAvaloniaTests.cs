using System;
using System.IO;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// A design set built against another major version of Avalonia than the designer runs is neither built
/// nor loaded, says why, and is lifted by a snapshot naming the version the designer runs (ADR 0034).
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostAvaloniaTests
{
    private static readonly Version Host = new(12, 1, 1, 0);

    [Fact]
    public void MeasureAvalonia_AnotherMajorVersion_IsUnsupportedAndSaysWhy()
    {
        (SolutionSnapshot snapshot, ProjectIdentity project) = Solution("11.3.2");

        (var diagnostics, string? unsupported) = ProjectDesignHost.MeasureAvalonia(snapshot, [project], Host);

        Assert.NotNull(unsupported);
        Assert.Contains("11.3.2", unsupported, StringComparison.Ordinal);
        Assert.Contains("12.1.1", unsupported, StringComparison.Ordinal);

        ProjectDiagnostic said = Assert.Single(diagnostics);

        Assert.Equal(ProjectDesignDiagnosticCodes.AvaloniaVersionUnsupported, said.Code);
        Assert.Equal(ProjectDiagnosticSeverity.Error, said.Severity);
    }

    [Fact]
    public void MeasureAvalonia_AnotherMinorVersion_IsAWarningAndLoads()
    {
        (SolutionSnapshot snapshot, ProjectIdentity project) = Solution("12.0.4");

        (var diagnostics, string? unsupported) = ProjectDesignHost.MeasureAvalonia(snapshot, [project], Host);

        Assert.Null(unsupported);

        ProjectDiagnostic said = Assert.Single(diagnostics);

        Assert.Equal(ProjectDesignDiagnosticCodes.AvaloniaVersionDiffers, said.Code);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, said.Severity);
    }

    [Theory]
    [InlineData("12.1.0")]
    [InlineData("12.1.7")]
    [InlineData("12.1.0-beta1")]
    [InlineData(null)]
    public void MeasureAvalonia_TheSameMinorVersionOrNothingRestoredYet_SaysNothing(string? version)
    {
        (SolutionSnapshot snapshot, ProjectIdentity project) = Solution(version);

        (var diagnostics, string? unsupported) = ProjectDesignHost.MeasureAvalonia(snapshot, [project], Host);

        Assert.Null(unsupported);
        Assert.Empty(diagnostics);
    }

    [AvaloniaFact]
    public async Task StartAsync_ADesignSetOnAnotherMajorVersion_BuildsAndLoadsNothingAndOpensDocumentsWithTheirText()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            static (fixtures, bench) =>
            {
                // Code newer than the output: a host that measured nothing would build first.
                fixtures.Write("Code.cs", "class Code { }");
                bench.Snapshot = request => fixtures.Snapshot(request, configure: project => Resolve(project, "11.3.2"));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectDesignState.Unsupported, stand.Host.State);
        Assert.Null(stand.Host.GenerationName);
        Assert.Contains("11.3.2", stand.Host.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains(stand.Host.GenerationDiagnostics, static d => d.Code == ProjectDesignDiagnosticCodes.AvaloniaVersionUnsupported);
        Assert.Empty(stand.Bench.Kinds);

        XamlLiveDocument document = await stand.Host.OpenDocumentAsync(
            stand.Fixtures.Document("FixtureControl.axaml"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(XamlLiveDocumentState.Detached, document.State);
        Assert.Contains("FixtureControl", document.Document.SourceText.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_CodeSavedWhileUnsupported_BuildsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            static (fixtures, bench) =>
            {
                fixtures.Write("Code.cs", "class Code { }");
                bench.Snapshot = request => fixtures.Snapshot(request, configure: project => Resolve(project, "11.3.2"));
            },
            TestContext.Current.CancellationToken);

        var changes = new EventProbe<ProjectDesignChangesEventArgs>();

        stand.Host.ChangesApplied += changes.Record;

        stand.Host.NotifyChanged([new FileChange(stand.Fixtures.Document("Code.cs"), FileChangeKind.Changed)]);

        await changes.WhenCountAsync(1);

        // Well past the quiet moment: a build the host scheduled would be running now.
        stand.Bench.Clock.Advance(TimeSpan.FromSeconds(5));

        await stand.Host.BuildInFlight;

        Assert.Empty(stand.Bench.Kinds);
        Assert.Equal(ProjectDesignState.Unsupported, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_TheProjectMovedToTheDesignersAvalonia_MakesTheGenerationAndShowsTheDocuments()
    {
        string version = "11.3.2";

        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, bench) => bench.Snapshot = request => fixtures.Snapshot(request, configure: project => Resolve(project, version)),
            TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.Host.OpenDocumentAsync(
            stand.Fixtures.Document("FixtureControl.axaml"),
            cancellationToken: TestContext.Current.CancellationToken);

        var swaps = new EventProbe<ProjectDesignSwapCompletedEventArgs>();
        var settled = new EventProbe<ProjectDesignChangesEventArgs>();

        stand.Host.SwapCompleted += swaps.Record;
        stand.Host.ChangesApplied += settled.Record;

        // Another unsupported version is measured again and changes nothing.
        version = "11.3.4";

        await stand.Bench.Workspace.RefreshAsync(TestContext.Current.CancellationToken);

        // The host deals with its work in order: a batch given after the snapshot is settled after it.
        stand.Host.NotifyChanged([new FileChange(stand.Fixtures.Document("FixtureControl.axaml"), FileChangeKind.Changed)]);

        await settled.WhenCountAsync(1);
        await stand.Host.SwapInFlight;

        Assert.Equal(ProjectDesignState.Unsupported, stand.Host.State);
        Assert.Contains("11.3.4", stand.Host.UnsupportedReason, StringComparison.Ordinal);
        Assert.Empty(swaps.Seen);

        version = $"{ProjectDesignHost.HostAvalonia.Major}.{ProjectDesignHost.HostAvalonia.Minor}.{ProjectDesignHost.HostAvalonia.Build}";

        await stand.Bench.Workspace.RefreshAsync(TestContext.Current.CancellationToken);
        await swaps.WhenCountAsync(1);

        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
        Assert.Null(stand.Host.UnsupportedReason);
        Assert.NotNull(stand.Host.GenerationName);
        Assert.Equal(XamlLiveDocumentState.Live, document.State);
    }

    private static void Resolve(ProjectSnapshotBuilder project, string version) =>
        project.ResolvedPackages.Add(new ResolvedPackage { PackageId = "Avalonia", Version = version, IsDirect = true });

    private static (SolutionSnapshot Snapshot, ProjectIdentity Project) Solution(string? avalonia)
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        CanonicalPath file = CanonicalPath.Create(Path.Combine(Path.GetTempPath(), "arxis-avalonia", "App", "App.csproj"));

        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, file),
            Name = "App",
            ProjectFilePath = file,
        };

        if (avalonia is not null)
        {
            Resolve(project, avalonia);
        }

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest { Workspace = workspace, EntryPointPath = file },
        };

        solution.Projects.Add(project.ToSnapshot());

        return (solution.ToSnapshot(), project.Identity);
    }
}
