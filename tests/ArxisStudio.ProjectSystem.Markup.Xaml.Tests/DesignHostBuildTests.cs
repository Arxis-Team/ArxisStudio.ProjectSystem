using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// The design host's own builds: after a quiet moment, one at a time, of the top projects of what
/// changed, restoring first when a restore is due.
/// </summary>
/// <remarks>
/// The clock is a fake one the test advances, and a build parks inside the bench when the test needs
/// one in flight; nothing here waits on time passing.
/// </remarks>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostBuildTests
{
    private static readonly DateTime LongAgo = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task StartAsync_AWorkspaceLoadedWithoutTheBuildProperties_Throws()
    {
        using var fixtures = new DesignFixtures();
        await using var bench = new DesignBench(fixtures);

        await bench.Workspace.LoadAsync(
            new WorkspaceLoadRequest { Workspace = bench.Workspace.Identity, EntryPointPath = fixtures.ProjectFile },
            TestContext.Current.CancellationToken);

        await using var host = new ProjectDesignHost(bench.Workspace, bench.Options());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.StartAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task StartAsync_CodeNewerThanTheOutput_IsBuiltFirst()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, _) => fixtures.Write("Code.cs", "class Code { }"),
            TestContext.Current.CancellationToken);

        Assert.Equal([ProjectOperationKind.Build], stand.Bench.Kinds);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task StartAsync_AnOutputNewerThanEverything_BuildsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(Code, TestContext.Current.CancellationToken);

        Assert.Empty(stand.Bench.Kinds);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_SavesWithinOneQuietMoment_BuildOnce()
    {
        await using DesignStand stand = await DesignStand.StartAsync(Code, TestContext.Current.CancellationToken);

        stand.Bench.BuildRewritesOutput = false;

        var changes = new EventProbe<ProjectDesignChangesEventArgs>();
        var builds = new EventProbe<ProjectDesignBuildCompletedEventArgs>();

        stand.Host.ChangesApplied += changes.Record;
        stand.Host.BuildCompleted += builds.Record;

        CanonicalPath code = stand.Fixtures.Document("Code.cs");

        stand.Host.NotifyChanged([new FileChange(code, FileChangeKind.Changed)]);
        await changes.WhenCountAsync(1);
        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(300));

        stand.Host.NotifyChanged([new FileChange(code, FileChangeKind.Changed)]);
        await changes.WhenCountAsync(2);
        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Empty(stand.Bench.Kinds);

        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await builds.WhenCountAsync(1);

        ProjectOperationRequest build = Assert.Single(stand.Bench.Operations);

        Assert.Equal(ProjectOperationKind.Build, build.Kind);
        Assert.Equal([stand.Fixtures.Project(stand.Bench.Workspace.Identity)], build.Projects);
        Assert.Equal("bin/ArxisStudio/", build.GlobalProperties["OutputPath"]);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_ASaveDuringABuild_IsBuiltAfterIt()
    {
        await using DesignStand stand = await DesignStand.StartAsync(Code, TestContext.Current.CancellationToken);

        stand.Bench.BuildRewritesOutput = false;
        stand.Bench.HoldOperations = true;

        var changes = new EventProbe<ProjectDesignChangesEventArgs>();
        var builds = new EventProbe<ProjectDesignBuildCompletedEventArgs>();

        stand.Host.ChangesApplied += changes.Record;
        stand.Host.BuildCompleted += builds.Record;

        CanonicalPath code = stand.Fixtures.Document("Code.cs");

        stand.Host.NotifyChanged([new FileChange(code, FileChangeKind.Changed)]);
        await changes.WhenCountAsync(1);
        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(400));

        HeldOperation first = await stand.Bench.NextArrivalAsync().WaitAsync(DesignStand.Patience);

        Assert.Equal(ProjectDesignState.Building, stand.Host.State);

        stand.Host.NotifyChanged([new FileChange(code, FileChangeKind.Changed)]);
        await changes.WhenCountAsync(2);
        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(400));

        first.Release();

        HeldOperation second = await stand.Bench.NextArrivalAsync().WaitAsync(DesignStand.Patience);

        second.Release();

        await builds.WhenCountAsync(2);

        Assert.Equal([ProjectOperationKind.Build, ProjectOperationKind.Build], stand.Bench.Kinds);
    }

    [AvaloniaFact]
    public async Task BuildAsync_AFailedBuild_KeepsTheGeneration()
    {
        await using DesignStand stand = await DesignStand.StartAsync(Code, TestContext.Current.CancellationToken);

        string? generation = stand.Host.GenerationName;

        stand.Bench.FailBuilds = true;

        ProjectDesignBuildResult result = await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.False(result.TypesChanged);
        Assert.Equal("CS0103", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
        Assert.Equal(generation, stand.Host.GenerationName);
    }

    [AvaloniaFact]
    public async Task BuildAsync_AnOutputTheBuildDidNotRewrite_SwapsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(Code, TestContext.Current.CancellationToken);

        string? generation = stand.Host.GenerationName;

        stand.Bench.BuildRewritesOutput = false;

        ProjectDesignBuildResult result = await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.False(result.TypesChanged);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
        Assert.Equal(generation, stand.Host.GenerationName);
    }

    [AvaloniaFact]
    public async Task BuildAsync_AProjectNeverRestored_RestoresFirstAndOnce()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, bench) =>
            {
                Code(fixtures, bench);

                bench.Snapshot = request => fixtures.Snapshot(
                    request, project => project.RestoreOutputs.Add(fixtures.Document("obj/project.assets.json")));
            },
            TestContext.Current.CancellationToken);

        stand.Bench.BuildRewritesOutput = false;

        ProjectDesignBuildResult first = await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);
        ProjectDesignBuildResult second = await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Restored);
        Assert.False(second.Restored);
        Assert.Equal([ProjectOperationKind.Restore, ProjectOperationKind.Build, ProjectOperationKind.Build], stand.Bench.Kinds);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AProjectFileSaved_RestoresBeforeTheNextBuild()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            Code,
            TestContext.Current.CancellationToken);

        stand.Bench.BuildRewritesOutput = false;

        var changes = new EventProbe<ProjectDesignChangesEventArgs>();
        var builds = new EventProbe<ProjectDesignBuildCompletedEventArgs>();

        stand.Host.ChangesApplied += changes.Record;
        stand.Host.BuildCompleted += builds.Record;

        stand.Host.NotifyChanged([new FileChange(stand.Fixtures.ProjectFile, FileChangeKind.Changed)]);
        await changes.WhenCountAsync(1);
        stand.Bench.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await builds.WhenCountAsync(1);

        Assert.Equal([ProjectOperationKind.Restore, ProjectOperationKind.Build], stand.Bench.Kinds);
        Assert.True(builds.Seen[0].Result.Restored);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_WhatARestoreWrote_IsNoReasonToRestoreAgain()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, bench) =>
            {
                Code(fixtures, bench);

                CanonicalPath assets = fixtures.Write("project.assets.json", "{}");

                File.SetLastWriteTimeUtc(assets.Value, LongAgo);

                bench.Snapshot = request => fixtures.Snapshot(
                    request,
                    project =>
                    {
                        project.EvaluationInputs.Add(assets);
                        project.RestoreOutputs.Add(assets);
                    });
            },
            TestContext.Current.CancellationToken);

        stand.Bench.BuildRewritesOutput = false;

        var changes = new EventProbe<ProjectDesignChangesEventArgs>();

        stand.Host.ChangesApplied += changes.Record;

        stand.Host.NotifyChanged([new FileChange(stand.Fixtures.Document("project.assets.json"), FileChangeKind.Changed)]);
        await changes.WhenCountAsync(1);

        await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([ProjectOperationKind.Build], stand.Bench.Kinds);
    }

    [Fact]
    public void TopsOf_ALibraryChanged_BuildsTheApplicationsThatReferenceIt()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        ProjectIdentity app = Identity(workspace, "App");
        ProjectIdentity lib = Identity(workspace, "Lib");
        ProjectIdentity tool = Identity(workspace, "Tool");
        ProjectIdentity other = Identity(workspace, "Other");

        SolutionSnapshot snapshot = Solution(
            workspace,
            Project(workspace, "App", "Lib"),
            Project(workspace, "Tool", "Lib"),
            Project(workspace, "Lib"),
            Project(workspace, "Other"));
        ImmutableArray<ProjectIdentity> set = [app, tool, lib, other];

        (ImmutableArray<ProjectIdentity> tops, ImmutableArray<ProjectIdentity> affected) = ProjectDesignHost.TopsOf(snapshot, [lib], set);

        Assert.Equal([app, tool], tops);
        Assert.Equal([app, tool, lib], affected);
        Assert.Equal([app], ProjectDesignHost.TopsOf(snapshot, [app], set).Tops);
        Assert.Equal([app, other], ProjectDesignHost.TopsOf(snapshot, [app, other], set).Tops);
        Assert.Empty(ProjectDesignHost.TopsOf(snapshot, [Identity(workspace, "Tests")], set).Tops);
    }

    [Fact]
    public void DesignSetOf_ProjectsWithForms_AreTheSetWithWhatTheyReferenceAndNoTests()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();

        SolutionSnapshot snapshot = Solution(
            workspace,
            Designed(workspace, "App", test: false, "Lib"),
            Project(workspace, "Lib"),
            Designed(workspace, "App.Tests", test: true, "App"),
            Project(workspace, "Tool"));

        Assert.Equal([Identity(workspace, "App"), Identity(workspace, "Lib")], ProjectDesignHost.DesignSetOf(snapshot));

        SolutionSnapshot formless = Solution(workspace, Project(workspace, "Lib"), Project(workspace, "Tool"));

        Assert.Equal([Identity(workspace, "Lib"), Identity(workspace, "Tool")], ProjectDesignHost.DesignSetOf(formless));
    }

    [Fact]
    public void CodeChanged_WhatARestoreWrites_IsNoCodeChange()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        CanonicalPath projectFile = TestPath("App", "App.csproj");
        CanonicalPath assets = TestPath("App", "obj", "project.assets.json");

        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, projectFile),
            Name = "App",
            ProjectFilePath = projectFile,
        };

        project.EvaluationInputs.Add(projectFile);
        project.EvaluationInputs.Add(assets);
        project.RestoreOutputs.Add(assets);

        SolutionSnapshot snapshot = Solution(workspace, project.ToSnapshot());

        ImmutableArray<FileChange> restored = [new FileChange(assets, FileChangeKind.Changed)];
        ImmutableArray<FileChange> edited = [new FileChange(projectFile, FileChangeKind.Changed)];

        Assert.Empty(ProjectDesignHost.CodeChanged(snapshot, snapshot.Classify(restored), restored));
        Assert.Equal([project.Identity], ProjectDesignHost.CodeChanged(snapshot, snapshot.Classify(edited), edited));
    }

    /// <summary>Gives the project a class, older than the fixtures' output, so starting builds nothing.</summary>
    private static void Code(DesignFixtures fixtures, DesignBench bench)
    {
        CanonicalPath code = fixtures.Write("Code.cs", "class Code { }");

        File.SetLastWriteTimeUtc(code.Value, LongAgo);
    }

    private static CanonicalPath TestPath(params string[] parts) =>
        CanonicalPath.Create(Path.Combine([Path.GetTempPath(), "arxis-tops", .. parts]));

    private static ProjectIdentity Identity(WorkspaceIdentity workspace, string name) =>
        ProjectIdentity.Create(workspace, TestPath(name, name + ".csproj"));

    private static ProjectSnapshot Project(WorkspaceIdentity workspace, string name, params string[] references)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(workspace, name),
            Name = name,
            ProjectFilePath = TestPath(name, name + ".csproj"),
        };

        foreach (string reference in references)
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = TestPath(reference, reference + ".csproj"),
                Project = Identity(workspace, reference),
            });
        }

        return project.ToSnapshot();
    }

    /// <summary>A project with a form, and whether it says it is a test project.</summary>
    private static ProjectSnapshot Designed(WorkspaceIdentity workspace, string name, bool test, params string[] references)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(workspace, name),
            Name = name,
            ProjectFilePath = TestPath(name, name + ".csproj"),
        };

        project.Items.Add(new ProjectItem { ItemType = "AvaloniaXaml", Include = "Form.axaml", FullPath = TestPath(name, "Form.axaml") });
        project.Properties["IsTestProject"] = test ? "true" : "false";

        foreach (string reference in references)
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = TestPath(reference, reference + ".csproj"),
                Project = Identity(workspace, reference),
            });
        }

        return project.ToSnapshot();
    }

    private static SolutionSnapshot Solution(WorkspaceIdentity workspace, params ProjectSnapshot[] projects)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = workspace,
            Name = "Solution",
            Request = new WorkspaceLoadRequest { Workspace = workspace, EntryPointPath = projects[0].ProjectFilePath },
        };

        foreach (ProjectSnapshot project in projects)
        {
            solution.Projects.Add(project);
        }

        return solution.ToSnapshot();
    }
}
