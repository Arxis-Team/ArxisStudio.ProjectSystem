using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Restore, build, rebuild and clean, run through the real engine.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is a plain MSBuild project with no SDK, and that is the whole reason these tests can
/// exist. An SDK-style project cannot be built without restoring it first — even one with no package
/// references — and a restore reaches the package cache and the network, which the contract forbids
/// a test to depend on. A project that defines its own targets needs none of that and still goes
/// through <c>BuildManager</c> exactly as a real one does.
/// </para>
/// <para>
/// Its targets are switched by properties, so failure and warning cases are the same fixture asked
/// to behave differently rather than four nearly-identical files.
/// </para>
/// </remarks>
public sealed class MSBuildOperationTests
{
    private static CanonicalPath Buildable =>
        CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Buildable", "Buildable.proj"));

    private static ProjectOperationRequest Request(
        ProjectOperationKind kind = ProjectOperationKind.Build,
        params (string Key, string Value)[] properties) => new()
        {
            Kind = kind,
            Workspace = WorkspaceIdentity.New(),
            EntryPointPath = Buildable,
            GlobalProperties = ProjectMetadata.Create(
                properties.Select(static p => new KeyValuePair<string, string>(p.Key, p.Value))),
        };

    private static async Task<ProjectOperationResult> ExecuteAsync(ProjectOperationRequest request) =>
        await new MSBuildProjectProvider().ExecuteAsync(
            request, progress: null, TestContext.Current.CancellationToken);

    [Fact]
    public void TheProvider_PerformsEveryKindItModels()
    {
        var provider = new MSBuildProjectProvider();

        Assert.True(provider.CanExecute(ProjectOperationKind.Restore));
        Assert.True(provider.CanExecute(ProjectOperationKind.Build));
        Assert.True(provider.CanExecute(ProjectOperationKind.Rebuild));
        Assert.True(provider.CanExecute(ProjectOperationKind.Clean));
    }

    [Theory]
    [InlineData(ProjectOperationKind.Restore)]
    [InlineData(ProjectOperationKind.Build)]
    [InlineData(ProjectOperationKind.Rebuild)]
    [InlineData(ProjectOperationKind.Clean)]
    public async Task EveryKind_RunsItsOwnTarget(ProjectOperationKind kind)
    {
        ProjectOperationResult result = await ExecuteAsync(Request(kind));

        Assert.True(
            result.Status == ProjectOperationStatus.Succeeded,
            $"{kind} failed:\n  " + string.Join("\n  ", result.Diagnostics));

        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// A compile error is an ordinary outcome, not an exception. It comes back as a failed result
    /// carrying the engine's own code, which is what a caller can act on.
    /// </summary>
    [Fact]
    public async Task AFailingBuild_IsAFailedResultWithTheEnginesCode()
    {
        ProjectOperationResult result = await ExecuteAsync(Request(properties: ("EmitError", "true")));

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.True(result.HasErrors);

        ProjectDiagnostic diagnostic = Assert.Single(
            result.Diagnostics.Where(static d => d.Code == "APSTEST02"));

        Assert.Equal(ProjectDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("MSBuild", diagnostic.ProviderName);
        Assert.Contains("asked to fail", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>A warning is reported and costs nothing: the work was still done.</summary>
    [Fact]
    public async Task AWarning_DoesNotFailTheOperation()
    {
        ProjectOperationResult result = await ExecuteAsync(Request(properties: ("EmitWarning", "true")));

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.False(result.HasErrors);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal("APSTEST01", diagnostic.Code);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public async Task ARequestedConfiguration_ReachesTheEngine()
    {
        ProjectOperationResult result = await ExecuteAsync(
            Request() with { Configuration = "Release" });

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task AMissingEntryPoint_IsADiagnosticNotAnException()
    {
        ProjectOperationResult result = await new MSBuildProjectProvider().ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = ProjectOperationKind.Build,
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = CanonicalPath.Create(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nowhere", "Nowhere.proj")),
            },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.Equal(MSBuildDiagnosticCodes.ProjectFileNotFound, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task AnAlreadyCancelledOperation_ThrowsWithoutBuilding()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new MSBuildProjectProvider().ExecuteAsync(
                Request(), progress: null, cancellation.Token));
    }

    /// <summary>
    /// Cancelling while the engine is running is the case a caller actually reaches, and the one
    /// with a race in it: the build can finish between the token being cancelled and the engine
    /// being told. Either way the caller hears the token, never a result and never MSBuild's own
    /// failure raised out of their call to <c>Cancel</c>.
    /// </summary>
    /// <remarks>
    /// The cancellation happens inside the project-started notification, which the engine raises on
    /// its own thread while the build is in progress. That is what makes this exact rather than
    /// timed: there is no waiting, and no window in which the test hopes to be fast enough.
    /// </remarks>
    [Fact]
    public async Task CancellingDuringTheBuild_ThrowsRatherThanReturningAResult()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new MSBuildProjectProvider().ExecuteAsync(
                Request(),
                new ImmediateProgress(_ => cancellation.Cancel()),
                cancellation.Token));
    }

    /// <summary>Project-started is the granularity a progress bar can use.</summary>
    /// <remarks>
    /// The engine raises its events on its own thread, so the list is guarded — but it raises them
    /// <em>during</em> the build, and this progress implementation reports on the thread that
    /// reported. So everything has landed by the time the operation returns, and there is nothing to
    /// wait for.
    /// </remarks>
    [Fact]
    public async Task Progress_ReportsWhatIsBeingBuilt()
    {
        var seen = new List<string>();

        ProjectOperationResult result = await new MSBuildProjectProvider().ExecuteAsync(
            Request(),
            new ImmediateProgress(p =>
            {
                lock (seen)
                {
                    seen.Add(p.Message);
                }
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);

        Assert.Contains(seen, static message => message.Contains("Buildable.proj", StringComparison.Ordinal));
    }

    /// <summary>
    /// Through the workspace, which routes to the provider by capability. Nothing is published: a
    /// build changed what is on disk, not what the project says.
    /// </summary>
    [Fact]
    public async Task ThroughAWorkspace_TheBuildRunsAndPublishesNothing()
    {
        await using var workspace = new ProjectWorkspace(new MSBuildProjectProvider());

        int raised = 0;
        workspace.SnapshotChanged += (_, _) => raised++;

        ProjectOperationResult result = await workspace.ExecuteAsync(
            new ProjectOperationRequest
            {
                Kind = ProjectOperationKind.Build,
                Workspace = workspace.Identity,
                EntryPointPath = Buildable,
            },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Equal(WorkspaceVersion.None, workspace.CurrentVersion);
        Assert.Null(workspace.CurrentSnapshot);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Reports on the thread that reported, unlike <see cref="Progress{T}"/>, which posts elsewhere
    /// and would turn "cancel while building" into "cancel at some point afterwards".
    /// </summary>
    private sealed class ImmediateProgress(Action<ProjectOperationProgress> onReport)
        : IProgress<ProjectOperationProgress>
    {
        public void Report(ProjectOperationProgress value) => onReport(value);
    }
}
