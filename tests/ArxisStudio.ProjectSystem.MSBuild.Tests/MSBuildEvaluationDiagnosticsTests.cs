using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// What the engine notices, and how it reaches a caller.
/// </summary>
/// <remarks>
/// Two mechanisms with different jobs, and the split was measured rather than assumed. A problem
/// that stops the evaluation arrives as an exception and becomes <c>APS2002</c>; a problem the
/// evaluation survives is only ever reported through MSBuild's logging service, and would otherwise
/// be lost entirely.
/// </remarks>
public sealed class MSBuildEvaluationDiagnosticsTests
{
    private static async Task<WorkspaceLoadResult> LoadAsync(string fixture) =>
        await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = CanonicalPath.Create(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture, fixture + ".csproj")),
            },
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The SDK contributes nearly everything a project has, so a project whose SDK cannot be found
    /// has not really evaluated. Reporting success with an almost-empty snapshot would be worse than
    /// reporting nothing.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableSdk_IsAnError()
    {
        WorkspaceLoadResult result = await LoadAsync("BadSdk");

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(MSBuildDiagnosticCodes.EvaluationFailed, diagnostic.Code);

        // MSBuild's own explanation is carried through, because it names the thing to fix.
        Assert.Contains("Definitely.Not.A.Real.Sdk", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingImport_IsAnError()
    {
        WorkspaceLoadResult result = await LoadAsync("MissingImport");

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Contains("NotThere.props", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duplicate import does not stop the evaluation, so the exception path never sees it. It
    /// arrives through the engine's logging service instead, keeping the engine's own code — which
    /// is what a caller can actually act on.
    /// </summary>
    [Fact]
    public async Task AProblemTheEvaluationSurvives_ArrivesWithTheEnginesOwnCode()
    {
        WorkspaceLoadResult result = await LoadAsync("DuplicateImport");

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);

        ProjectDiagnostic diagnostic = Assert.Single(
            result.Diagnostics.Where(static d => d.Code == "MSB4011"));

        Assert.Equal(ProjectDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("MSBuild", diagnostic.ProviderName);
        Assert.False(diagnostic.FilePath.IsEmpty);

        // A warning does not cost the snapshot: the project is there and usable.
        Assert.Equal("DuplicateImport", Assert.Single(result.Snapshot!.Projects).Name);
    }

    [Fact]
    public async Task AHealthyProject_SaysNothing()
    {
        WorkspaceLoadResult result = await LoadAsync("Basic");

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
    }
}
