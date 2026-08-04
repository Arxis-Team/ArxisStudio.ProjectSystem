using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class LoadTests
{
    private static readonly WorkspaceIdentity Workspace = WorkspaceIdentity.New();

    [Fact]
    public void ARequestWithoutAWorkspace_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceLoadRequest
        {
            Workspace = WorkspaceIdentity.None,
            EntryPointPath = TestPaths.Project(),
        });
    }

    [Fact]
    public void ARequestWithNothingToOpen_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceLoadRequest
        {
            Workspace = Workspace,
            EntryPointPath = CanonicalPath.None,
        });
    }

    [Fact]
    public void ARequest_ClassifiesItsOwnEntryPoint()
    {
        WorkspaceLoadRequest request = Request();

        Assert.Equal(WorkspaceEntryPointKind.Project, request.EntryPoint.Kind);
        Assert.Equal(request.EntryPointPath, request.EntryPoint.Path);
    }

    [Fact]
    public void ARequest_DefaultsToIncludingEverything()
    {
        WorkspaceLoadRequest request = Request();

        Assert.Same(WorkspaceLoadOptions.Default, request.Options);
        Assert.True(request.Options.IncludeItems);
        Assert.True(request.Options.IncludeOutputArtifacts);
        Assert.Same(ProjectMetadata.Empty, request.GlobalProperties);
        Assert.Null(request.Configuration);
    }

    [Fact]
    public void ARequest_CanBeCopiedWithADifferentConfiguration()
    {
        WorkspaceLoadRequest request = Request();
        WorkspaceLoadRequest release = request with { Configuration = "Release" };

        Assert.Null(request.Configuration);
        Assert.Equal("Release", release.Configuration);
        Assert.Equal(request.EntryPointPath, release.EntryPointPath);
    }

    [Fact]
    public void ASnapshotWithNoErrors_Succeeds()
    {
        WorkspaceLoadResult result = WorkspaceLoadResult.Success(Solution());

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.True(result.HasSnapshot);
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Snapshot);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ASnapshotWithAnError_SucceedsWithErrors()
    {
        WorkspaceLoadResult result = WorkspaceLoadResult.Success(
            Solution(), [Error("APS1002", "something broke")]);

        Assert.Equal(WorkspaceLoadStatus.SucceededWithErrors, result.Status);
        Assert.True(result.HasSnapshot);
        Assert.True(result.HasErrors);
        Assert.NotNull(result.Snapshot);
    }

    [Fact]
    public void ASnapshotWithOnlyAWarning_StillSucceeds()
    {
        WorkspaceLoadResult result = WorkspaceLoadResult.Success(
            Solution(),
            [new ProjectDiagnostic("APS1002", "just a warning", ProjectDiagnosticSeverity.Warning)]);

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.False(result.HasErrors);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void NoSnapshotAndAnError_Fails()
    {
        WorkspaceLoadResult result = WorkspaceLoadResult.Failure(Error("APS1001", "nothing can open this"));

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.False(result.HasSnapshot);
        Assert.True(result.HasErrors);
        Assert.Null(result.Snapshot);
    }

    /// <summary>
    /// The fourth cell of the table on <see cref="WorkspaceLoadResult"/>. A consumer shown "it
    /// failed" with nothing to display has been told nothing, so the state is refused rather than
    /// represented.
    /// </summary>
    [Fact]
    public void NoSnapshotAndNoError_CannotBeConstructed()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceLoadResult.Failure([]));

        Assert.Throws<ArgumentException>(() => WorkspaceLoadResult.Failure(
            [new ProjectDiagnostic("APS1001", "only a warning", ProjectDiagnosticSeverity.Warning)]));
    }

    /// <summary>
    /// The failure the whole design exists to prevent: a solution that loaded, with one broken
    /// project inside it, must not report unqualified success.
    /// </summary>
    [Fact]
    public void AnErrorOnAProject_ReachesTheResultStatus()
    {
        ProjectSnapshotBuilder project = ProjectBuilder();
        project.Diagnostics.Add(Error("APS1002", "this project is broken"));

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        solution.Projects.Add(project.ToSnapshot());

        WorkspaceLoadResult result = WorkspaceLoadResult.Success(solution.ToSnapshot());

        Assert.Equal(WorkspaceLoadStatus.SucceededWithErrors, result.Status);
        Assert.True(result.HasErrors);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void SolutionLevelDiagnostics_AreFlattenedIntoTheResult()
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        solution.Diagnostics.Add(Error("APS1003", "solution level"));
        solution.Projects.Add(ProjectBuilder().ToSnapshot());

        WorkspaceLoadResult result = WorkspaceLoadResult.Success(solution.ToSnapshot());

        Assert.Single(result.Diagnostics);
        Assert.Equal("APS1003", result.Diagnostics[0].Code);
    }

    [Fact]
    public void SnapshotIsNull_ExactlyWhenTheLoadFailed()
    {
        Assert.NotNull(WorkspaceLoadResult.Success(Solution()).Snapshot);
        Assert.NotNull(WorkspaceLoadResult.Success(Solution(), [Error("APS1002", "e")]).Snapshot);
        Assert.Null(WorkspaceLoadResult.Failure(Error("APS1001", "e")).Snapshot);
    }

    [Fact]
    public void Success_WithoutASnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WorkspaceLoadResult.Success(null!));
        Assert.Throws<ArgumentNullException>(() => WorkspaceLoadResult.Failure((System.Collections.Generic.IEnumerable<ProjectDiagnostic>)null!));
        Assert.Throws<ArgumentNullException>(() => WorkspaceLoadResult.Failure((ProjectDiagnostic)null!));
    }

    [Fact]
    public void WithSnapshot_KeepsTheStatusAndDiagnostics()
    {
        WorkspaceLoadResult result = WorkspaceLoadResult.Success(Solution(), [Error("APS1002", "e")]);
        WorkspaceLoadResult published = result.WithSnapshot(result.Snapshot!.WithVersion(WorkspaceVersion.Initial));

        Assert.Equal(result.Status, published.Status);
        Assert.Equal(result.Diagnostics, published.Diagnostics);
        Assert.Equal(WorkspaceVersion.Initial, published.Snapshot!.Version);
        Assert.Same(result, result.WithSnapshot(result.Snapshot!));
    }

    /// <summary>
    /// Cancellation crosses the provider boundary as an exception, not as a status or a
    /// diagnostic — which is what keeps <see cref="WorkspaceLoadStatus"/> exhaustive.
    /// </summary>
    [Fact]
    public async Task CancellationPropagatesThroughTheProviderBoundary()
    {
        IProjectSystemProvider provider = new CancellingProvider();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await provider.LoadAsync(Request(), cancellation.Token));
    }

    [Fact]
    public void AProviderDecidesWhatItCanOpen()
    {
        IProjectSystemProvider provider = new CancellingProvider();

        Assert.Equal("Cancelling", provider.Name);
        Assert.True(provider.CanLoad(WorkspaceEntryPoint.FromPath(TestPaths.Project())));
    }

    private static ProjectDiagnostic Error(string code, string message) =>
        new(code, message, ProjectDiagnosticSeverity.Error);

    private static WorkspaceLoadRequest Request() => new()
    {
        Workspace = Workspace,
        EntryPointPath = TestPaths.Project(),
    };

    private static ProjectSnapshotBuilder ProjectBuilder() => new()
    {
        Identity = ProjectIdentity.Create(Workspace, TestPaths.Project()),
        Name = "App",
        ProjectFilePath = TestPaths.Project(),
    };

    private static SolutionSnapshot Solution()
    {
        var builder = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        builder.Projects.Add(ProjectBuilder().ToSnapshot());

        return builder.ToSnapshot();
    }

    private sealed class CancellingProvider : IProjectSystemProvider
    {
        public string Name => "Cancelling";

        public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

        public ValueTask<WorkspaceLoadResult> LoadAsync(
            WorkspaceLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(WorkspaceLoadResult.Failure(Error("APS1002", "unreachable")));
        }
    }
}
