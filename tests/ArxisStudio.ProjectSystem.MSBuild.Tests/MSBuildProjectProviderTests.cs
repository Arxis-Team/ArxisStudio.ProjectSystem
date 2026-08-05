using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// The provider end to end, against project files that are evaluated for real.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately few. The mapping is covered exhaustively and without MSBuild in
/// <see cref="MSBuildProjectTranslatorTests"/>; what is left to prove here is the wiring — that a
/// real project evaluates, that the adapter copies the right fields out of it, and that the failures
/// come back as diagnostics rather than exceptions.
/// </para>
/// <para>
/// The fixtures declare package references and never restore them, because evaluation reads what a
/// project says rather than what NuGet resolved. So nothing here touches the network or the package
/// cache, and <c>Fixtures/Directory.Build.props</c> stops MSBuild's upward walk before it reaches
/// this repository's own settings.
/// </para>
/// </remarks>
public sealed class MSBuildProjectProviderTests
{
    private static CanonicalPath Fixture(string name) =>
        CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", name, name + ".csproj"));

    private static WorkspaceLoadRequest Request(string fixture, WorkspaceIdentity? workspace = null) => new()
    {
        Workspace = workspace ?? WorkspaceIdentity.New(),
        EntryPointPath = Fixture(fixture),
    };

    /// <summary>
    /// Asserts a load succeeded, and says what went wrong when it did not.
    /// </summary>
    /// <remarks>
    /// An integration test that fails with "expected Succeeded, actual Failed" has told nobody
    /// anything. The diagnostics are the whole explanation, so they belong in the message.
    /// </remarks>
    private static SolutionSnapshot Succeeded(WorkspaceLoadResult result)
    {
        Assert.True(
            result.Status != WorkspaceLoadStatus.Failed,
            "The load failed:\n  " + string.Join("\n  ", result.Diagnostics));

        return result.Snapshot!;
    }

    [Fact]
    public void CanLoad_AcceptsWhatMSBuildUnderstandsAndNothingElse()
    {
        var provider = new MSBuildProjectProvider();

        Assert.Equal("MSBuild", provider.Name);
        Assert.True(provider.CanLoad(WorkspaceEntryPoint.FromPath(Fixture("Basic"))));

        // Anything unrecognised is declined rather than attempted, so a workspace with several
        // providers can offer it to one that does know the format.
        Assert.False(provider.CanLoad(WorkspaceEntryPoint.FromPath(
            CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "notes.txt")))));
        Assert.False(provider.CanLoad(WorkspaceEntryPoint.None));
    }

    [Fact]
    public async Task ARealProject_Evaluates()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Basic"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal("Basic", project.Name);
        Assert.Equal("C#", project.Language);
        Assert.Equal("Library", project.Kind);
        Assert.Equal(["net10.0"], project.TargetFrameworks);
        Assert.Equal("BasicAssembly", project.Properties["AssemblyName"]);
        Assert.Equal("Basic.Namespace", project.Properties["RootNamespace"]);
        Assert.Equal("MSBuild", project.ProviderName);
    }

    /// <summary>
    /// The claim ADR 0003 makes, at the one place it can actually be checked: the evaluation's
    /// ProjectCollection is disposed before the provider returns, so a snapshot that still reads
    /// afterwards is a snapshot that owns its data.
    /// </summary>
    [Fact]
    public async Task ASnapshot_OutlivesTheEvaluationThatProducedIt()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("WithReferences"), TestContext.Current.CancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.NotEmpty(project.Properties);
        Assert.All(project.Items, static item => Assert.NotEmpty(item.ItemType));
        Assert.NotEmpty(project.PackageReferences);
    }

    [Fact]
    public async Task ACrossTargetingProject_ReportsEveryFramework()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("CrossTargeting"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal(["net10.0", "net8.0"], project.TargetFrameworks);
        Assert.Equal(["Debug", "Release", "Profiled"], project.Configurations);
    }

    /// <summary>
    /// A cross-targeting project's outer evaluation is not a build of anything: it says which
    /// frameworks exist and has no output path. Answering "where is the assembly" with nothing is
    /// no better than answering it with a guess, so the provider evaluates again inside one
    /// framework and says which one it picked.
    /// </summary>
    [Fact]
    public async Task ACrossTargetingProject_StillReportsAnOutputAndSaysWhichFrameworkItIsFor()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("CrossTargeting"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal("net10.0", project.ActiveTargetFramework);

        OutputArtifact assembly = Assert.Single(
            project.Outputs.Where(static o => o.Kind == OutputArtifactKind.Assembly));

        Assert.Equal("net10.0", assembly.TargetFramework);
        Assert.Contains("net10.0", assembly.Path.Value, StringComparison.Ordinal);

        // Choosing one framework must not hide the others.
        Assert.Equal(["net10.0", "net8.0"], project.TargetFrameworks);
    }

    [Fact]
    public async Task ARequestedFramework_DecidesWhichOutputComesBack()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            Request("CrossTargeting") with { TargetFramework = "net8.0" },
            TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal("net8.0", project.ActiveTargetFramework);

        OutputArtifact assembly = Assert.Single(
            project.Outputs.Where(static o => o.Kind == OutputArtifactKind.Assembly));

        Assert.Equal("net8.0", assembly.TargetFramework);
        Assert.Contains("net8.0", assembly.Path.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single-target project is already an explicit context, so nothing re-evaluates it.
    /// </summary>
    [Fact]
    public async Task ASingleTargetProject_IsEvaluatedOnce()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Basic"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal("net10.0", project.ActiveTargetFramework);
        Assert.Single(project.Outputs.Where(static o => o.Kind == OutputArtifactKind.Assembly));
    }

    /// <summary>
    /// The artifacts a consumer needs to assemble a runtime environment, read from a real
    /// evaluation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one is deliberately end to end rather than over a hand-built evaluation, because the
    /// half it checks is the half a pure translation test cannot see: the evaluator copies only an
    /// allow-listed set of properties out of MSBuild, so a translation that reads a property nobody
    /// surfaced is correct and dead at the same time. That is not hypothetical — it is exactly what
    /// happened when these artifacts were added.
    /// </para>
    /// <para>
    /// A library, so what is <em>absent</em> matters as much: an ordinary library carries a
    /// populated <c>ProjectRuntimeConfigFilePath</c> and emits no such file.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Outputs_DescribeARealProjectsArtifacts()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Basic"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal(
            [
                OutputArtifactKind.Assembly,
                OutputArtifactKind.SymbolFile,
                OutputArtifactKind.DependencyManifest,
                OutputArtifactKind.ReferenceAssembly,
            ],
            project.Outputs.Select(static o => o.Kind).Order());

        Assert.EndsWith(
            "BasicAssembly.pdb",
            Single(project, OutputArtifactKind.SymbolFile).Path.Value,
            StringComparison.Ordinal);

        Assert.EndsWith(
            "BasicAssembly.deps.json",
            Single(project, OutputArtifactKind.DependencyManifest).Path.Value,
            StringComparison.Ordinal);

        // The reference assembly is the one a compiler wants and the primary output is the one a
        // loader wants, and they are different files in different directories. Reporting either as
        // "the output" would answer half the callers wrongly.
        Assert.NotEqual(
            Single(project, OutputArtifactKind.Assembly).Path,
            Single(project, OutputArtifactKind.ReferenceAssembly).Path);

        Assert.All(project.Outputs, static o => Assert.Equal("net10.0", o.TargetFramework));
    }

    private static OutputArtifact Single(ProjectSnapshot project, OutputArtifactKind kind) =>
        Assert.Single(project.Outputs.Where(o => o.Kind == kind));

    [Fact]
    public async Task DeclaredReferences_ComeThroughWithoutRestoring()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("WithReferences"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        PackageReferenceInfo package = Assert.Single(
            project.PackageReferences.Where(static p => p.PackageId == "Serilog"));

        Assert.Equal("4.1.0", package.VersionText);
        Assert.Equal("all", package.PrivateAssets);

        ProjectReferenceInfo reference = Assert.Single(project.ProjectReferences);

        Assert.Equal(Fixture("Basic"), reference.ProjectFilePath);
        Assert.Equal(["basic"], reference.Aliases);
        Assert.True(reference.Project.IsEmpty);

        Assert.Contains(project.FrameworkReferences, static f => f.Name == "Microsoft.AspNetCore.App");

        AssemblyReferenceInfo assembly = Assert.Single(
            project.AssemblyReferences.Where(static a => a.Name == "Legacy"));

        Assert.EndsWith("Legacy.dll", assembly.HintPath.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The provider finds the restore output where the evaluation says it is, and what it read
    /// reaches the snapshot. The fixture's assets file is written by hand and names no package
    /// folder, so this asserts what restore resolved rather than where the files would be — path
    /// resolution is covered exhaustively in <see cref="RestoreAssetsReaderTests"/>.
    /// </summary>
    [Fact]
    public async Task WhatRestoreResolved_ReachesTheSnapshot()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Restored"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Equal(2, project.ResolvedPackages.Length);

        ResolvedPackage direct = project.ResolvedPackages.Single(static p => p.PackageId == "Serilog");

        Assert.Equal("4.1.0", direct.Version);
        Assert.True(direct.IsDirect);
        Assert.Equal(["Serilog.Sinks.Console"], direct.Dependencies);

        // The transitive one arrived on its own and the project never named it.
        Assert.False(project.ResolvedPackages.Single(static p => p.PackageId == "Serilog.Sinks.Console").IsDirect);

        // Declared and resolved are different questions, and both are answered.
        Assert.Equal("Serilog", Assert.Single(project.PackageReferences).PackageId);

        Assert.Empty(project.Diagnostics);
    }

    /// <summary>
    /// The ordinary state of a freshly cloned repository. A warning rather than an error: everything
    /// the project declares is there, only what restore would have resolved is missing.
    /// </summary>
    [Fact]
    public async Task AProjectWithPackagesAndNoRestore_SaysSo()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("WithReferences"), TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        ProjectDiagnostic diagnostic = Assert.Single(project.Diagnostics);

        Assert.Equal(MSBuildDiagnosticCodes.RestoreAssetsMissing, diagnostic.Code);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.False(diagnostic.IsError);
        Assert.Empty(project.ResolvedPackages);

        // A warning is not a failure: the snapshot is published and the load succeeded.
        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
    }

    /// <summary>
    /// A project with no packages is not waiting for a restore, and saying so would be noise in
    /// every solution that contains one.
    /// </summary>
    [Fact]
    public async Task AProjectWithoutPackages_IsNotToldToRestore()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Basic"), TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(Succeeded(result).Projects).Diagnostics);
    }

    [Fact]
    public async Task AMalformedProject_IsADiagnosticNotAnException()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider()
            .LoadAsync(Request("Malformed"), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Null(result.Snapshot);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(MSBuildDiagnosticCodes.EvaluationFailed, diagnostic.Code);
        Assert.Equal("MSBuild", diagnostic.ProviderName);
        Assert.Equal(Fixture("Malformed"), diagnostic.FilePath);
    }

    [Fact]
    public async Task AMissingProject_IsItsOwnDiagnostic()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = CanonicalPath.Create(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nothing", "Nothing.csproj")),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(MSBuildDiagnosticCodes.ProjectFileNotFound, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ARequestedConfiguration_ReachesTheEvaluation()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            Request("Basic") with { Configuration = "Release" },
            TestContext.Current.CancellationToken);

        Assert.Equal("Release", Assert.Single(Succeeded(result).Projects).ActiveConfiguration);
    }

    [Fact]
    public async Task ExcludingItems_KeepsTheReferences()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            Request("WithReferences") with
            {
                Options = new WorkspaceLoadOptions { IncludeItems = false },
            },
            TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(Succeeded(result).Projects);

        Assert.Empty(project.Items);
        Assert.NotEmpty(project.PackageReferences);
        Assert.NotEmpty(project.ProjectReferences);
    }

    [Fact]
    public async Task ACancelledLoad_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new MSBuildProjectProvider().LoadAsync(Request("Basic"), cancellation.Token));
    }

    /// <summary>
    /// Through the workspace, which is the way a consumer will actually meet it: one publication,
    /// one version, and a snapshot carrying the project.
    /// </summary>
    [Fact]
    public async Task ThroughAWorkspace_TheProjectIsPublished()
    {
        await using var workspace = new ProjectWorkspace(new MSBuildProjectProvider());

        WorkspaceLoadResult result = await workspace.LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = workspace.Identity,
                EntryPointPath = Fixture("Basic"),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.Same(result.Snapshot, workspace.CurrentSnapshot);

        Assert.True(workspace.CurrentSnapshot!.TryGetProject(Fixture("Basic"), out ProjectSnapshot? project));
        Assert.Equal("Basic", project.Name);
        Assert.Equal(workspace.Identity, project.Identity.Workspace);
    }
}
