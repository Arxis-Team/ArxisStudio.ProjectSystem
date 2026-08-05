using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Restore, build and their kin: the request, the result's invariants, and how a workspace routes
/// one to a provider that can do it.
/// </summary>
public sealed class OperationTests
{
    private static ProjectDiagnostic Error(string message = "it broke") =>
        new("APS3001", message, ProjectDiagnosticSeverity.Error);

    private static ProjectDiagnostic Warning(string message = "be careful") =>
        new("APS3002", message, ProjectDiagnosticSeverity.Warning);

    private static ProjectOperationRequest Request(ProjectWorkspace workspace) => new()
    {
        Kind = ProjectOperationKind.Build,
        Workspace = workspace.Identity,
        EntryPointPath = TestPaths.Project(),
    };

    [Fact]
    public void ARequestWithoutAWorkspaceOrAnEntryPoint_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProjectOperationRequest
        {
            Kind = ProjectOperationKind.Build,
            Workspace = WorkspaceIdentity.None,
            EntryPointPath = TestPaths.Project(),
        });

        Assert.Throws<ArgumentException>(() => new ProjectOperationRequest
        {
            Kind = ProjectOperationKind.Build,
            Workspace = WorkspaceIdentity.New(),
            EntryPointPath = CanonicalPath.None,
        });
    }

    [Fact]
    public void ARequest_ClassifiesItsEntryPointAndDefaultsToEverything()
    {
        var request = new ProjectOperationRequest
        {
            Kind = ProjectOperationKind.Restore,
            Workspace = WorkspaceIdentity.New(),
            EntryPointPath = TestPaths.Project(),
        };

        Assert.Equal(WorkspaceEntryPointKind.Project, request.EntryPoint.Kind);
        Assert.False(request.Projects.IsDefault);
        Assert.Empty(request.Projects);
        Assert.Same(ProjectMetadata.Empty, request.GlobalProperties);
        Assert.StartsWith("Restore ", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AResultWithNoErrors_Succeeded()
    {
        ProjectOperationResult result = ProjectOperationResult.Succeeded([Warning()]);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.False(result.HasErrors);
        Assert.Single(result.Diagnostics);
    }

    /// <summary>
    /// The invariant this type exists for: an outcome that disagrees with its own evidence cannot be
    /// constructed, in either direction.
    /// </summary>
    [Fact]
    public void AResultCannotDisagreeWithItsDiagnostics()
    {
        Assert.Throws<ArgumentException>(() => ProjectOperationResult.Succeeded([Error()]));
        Assert.Throws<ArgumentException>(() => ProjectOperationResult.Failed([Warning()]));
        Assert.Throws<ArgumentException>(() => ProjectOperationResult.Failed([]));
        Assert.Throws<ArgumentNullException>(() => ProjectOperationResult.Failed((IEnumerable<ProjectDiagnostic>)null!));
        Assert.Throws<ArgumentNullException>(() => ProjectOperationResult.Failed((ProjectDiagnostic)null!));
    }

    [Fact]
    public void AFailedResult_CarriesWhyItFailed()
    {
        ProjectOperationResult result = ProjectOperationResult.Failed(Error("the compiler said no"));

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.True(result.HasErrors);
        Assert.Equal("the compiler said no", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void AnEmptySuccess_HasNoDiagnosticsRatherThanDefault()
    {
        ProjectOperationResult result = ProjectOperationResult.Succeeded();

        Assert.False(result.Diagnostics.IsDefault);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("Succeeded", result.ToString());
    }

    /// <summary>
    /// Building is optional. A provider that reads projects need not build them, and asking anyway
    /// is an ordinary situation a host handles rather than a broken configuration.
    /// </summary>
    [Fact]
    public async Task WithNoProviderThatCanDoIt_TheAnswerIsADiagnostic()
    {
        var workspace = new ProjectWorkspace(new ControllableProvider());

        ProjectOperationResult result = await workspace.ExecuteAsync(
            Request(workspace), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.Equal(ProjectDiagnosticCodes.UnsupportedOperation, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task AProviderThatDeclinesThisKind_IsNotAsked()
    {
        var provider = new BuildingProvider { Kinds = [ProjectOperationKind.Restore] };
        var workspace = new ProjectWorkspace(provider);

        ProjectOperationResult result = await workspace.ExecuteAsync(
            Request(workspace) with { Kind = ProjectOperationKind.Build },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectDiagnosticCodes.UnsupportedOperation, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, provider.Executed);
    }

    [Fact]
    public async Task AnOperation_ReachesTheProviderWithThisWorkspacesIdentity()
    {
        var provider = new BuildingProvider();
        var workspace = new ProjectWorkspace(provider);

        ProjectOperationResult result = await workspace.ExecuteAsync(
            Request(workspace) with { Workspace = WorkspaceIdentity.New() },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Equal(workspace.Identity, provider.LastRequest!.Workspace);
        Assert.Equal(ProjectOperationKind.Build, provider.LastRequest.Kind);
    }

    [Fact]
    public async Task Progress_ReachesTheCaller()
    {
        var provider = new BuildingProvider
        {
            Report = ["compiling Core", "compiling App"],
        };

        var workspace = new ProjectWorkspace(provider);
        var seen = new List<string>();

        // Deliberately not Progress<T>, which posts to a captured context and would make this a race
        // the test hopes to win. IProgress<T> promises nothing about threading, so an implementation
        // that reports on the reporting thread is a legitimate one -- and the only kind that makes
        // the assertion exact.
        await workspace.ExecuteAsync(
            Request(workspace),
            new ImmediateProgress(p => seen.Add(p.Message)),
            TestContext.Current.CancellationToken);

        Assert.Equal(["compiling Core", "compiling App"], seen);
    }

    [Fact]
    public async Task AProviderThatThrows_BecomesADiagnostic()
    {
        var provider = new BuildingProvider { Throw = new InvalidOperationException("the engine fell over") };
        var workspace = new ProjectWorkspace(provider);

        ProjectOperationResult result = await workspace.ExecuteAsync(
            Request(workspace), progress: null, TestContext.Current.CancellationToken);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(ProjectDiagnosticCodes.ProviderFailed, diagnostic.Code);
        Assert.Contains("fell over", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderThatReturnsNothing_BecomesADiagnostic()
    {
        var provider = new BuildingProvider { ReturnNull = true };
        var workspace = new ProjectWorkspace(provider);

        ProjectOperationResult result = await workspace.ExecuteAsync(
            Request(workspace), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectDiagnosticCodes.InvalidProviderResult, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ACancelledOperation_Throws()
    {
        var workspace = new ProjectWorkspace(new BuildingProvider());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await workspace.ExecuteAsync(Request(workspace), progress: null, cancellation.Token));
    }

    [Fact]
    public async Task AnOperationAfterDisposal_Throws()
    {
        var workspace = new ProjectWorkspace(new BuildingProvider());
        ProjectOperationRequest request = Request(workspace);

        await workspace.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await workspace.ExecuteAsync(request, progress: null, CancellationToken.None));
    }

    /// <summary>
    /// An operation changes what is on disk, not what a project says. Publishing a snapshot for one
    /// would claim the model had changed when nothing had been re-read.
    /// </summary>
    [Fact]
    public async Task AnOperation_PublishesNothing()
    {
        var provider = new BuildingProvider();
        var workspace = new ProjectWorkspace(provider);

        int raised = 0;
        workspace.SnapshotChanged += (_, _) => raised++;

        await workspace.ExecuteAsync(Request(workspace), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceVersion.None, workspace.CurrentVersion);
        Assert.Null(workspace.CurrentSnapshot);
        Assert.Equal(0, raised);
        Assert.False(workspace.IsMutationHeld);
    }

    /// <summary>Reports on the thread that reported, so a test can assert without waiting.</summary>
    private sealed class ImmediateProgress(Action<ProjectOperationProgress> onReport)
        : IProgress<ProjectOperationProgress>
    {
        public void Report(ProjectOperationProgress value) => onReport(value);
    }

    /// <summary>A provider that reads projects and also does something to them.</summary>
    private sealed class BuildingProvider : IProjectSystemProvider, IProjectOperationProvider
    {
        public string Name => "Building";

        public IReadOnlyList<ProjectOperationKind>? Kinds { get; init; }

        public IReadOnlyList<string> Report { get; init; } = [];

        public Exception? Throw { get; init; }

        public bool ReturnNull { get; init; }

        public int Executed { get; private set; }

        public ProjectOperationRequest? LastRequest { get; private set; }

        public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

        public ValueTask<WorkspaceLoadResult> LoadAsync(
            WorkspaceLoadRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This provider exists for operations.");

        public bool CanExecute(ProjectOperationKind kind) =>
            Kinds is null || System.Linq.Enumerable.Contains(Kinds, kind);

        public ValueTask<ProjectOperationResult> ExecuteAsync(
            ProjectOperationRequest request,
            IProgress<ProjectOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            Executed++;
            LastRequest = request;

            if (Throw is not null)
            {
                throw Throw;
            }

            foreach (string message in Report)
            {
                progress?.Report(new ProjectOperationProgress { Message = message });
            }

            return ValueTask.FromResult(ReturnNull ? null! : ProjectOperationResult.Succeeded());
        }
    }
}
