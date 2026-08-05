using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Opening a whole solution: discovery, folders, configurations and the project graph.
/// </summary>
/// <remarks>
/// Both formats are exercised by the same theories wherever the answer should not depend on the
/// format, which is nearly everywhere. That is the point of reading them through one library:
/// MSBuild's own <c>SolutionFile</c> would have given a different answer for <c>.slnx</c>, and a
/// test suite that only checked <c>.sln</c> would not have noticed.
/// </remarks>
public sealed class MSBuildSolutionTests
{
    public static TheoryData<string> BothFormats => ["Suite.sln", "Suite.slnx"];

    private static CanonicalPath Solution(string file) =>
        CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Solution", file));

    private static CanonicalPath Project(string name) =>
        CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", name, name + ".csproj"));

    private static async Task<SolutionSnapshot> OpenAsync(string file)
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = Solution(file),
            },
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status != WorkspaceLoadStatus.Failed,
            "The load failed:\n  " + string.Join("\n  ", result.Diagnostics));

        return result.Snapshot!;
    }

    [Fact]
    public void CanLoad_AcceptsBothSolutionFormats()
    {
        var provider = new MSBuildProjectProvider();

        Assert.True(provider.CanLoad(WorkspaceEntryPoint.FromPath(Solution("Suite.sln"))));
        Assert.True(provider.CanLoad(WorkspaceEntryPoint.FromPath(Solution("Suite.slnx"))));
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task ASolution_DiscoversItsProjects(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        Assert.Equal(2, solution.Projects.Length);
        Assert.True(solution.TryGetProject(Project("Basic"), out ProjectSnapshot? basic));
        Assert.True(solution.TryGetProject(Project("WithReferences"), out _));
        Assert.Equal("Basic", basic.Name);
        Assert.Equal(["net10.0"], basic.TargetFrameworks);
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task ASolution_HasAnIdentityAndAName(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        Assert.False(solution.Solution.IsEmpty);
        Assert.Equal(Solution(file), solution.Solution.SolutionFilePath);
        Assert.Equal("Suite", solution.Name);
        Assert.Equal(solution.Workspace, solution.Solution.Workspace);
    }

    /// <summary>
    /// The reason this package does not use MSBuild's <c>SolutionFile</c>: it returns no folders at
    /// all for <c>.slnx</c>, while leaving the projects pointing at one. Reading both formats
    /// through one library is what makes this theory able to run twice.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task SolutionFolders_SurviveBothFormats(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        SolutionFolder folder = Assert.Single(solution.Folders);

        Assert.Equal("Libraries", folder.Name);
        Assert.Equal("/Libraries/", folder.Path);

        Assert.True(solution.TryGetProject(Project("Basic"), out ProjectSnapshot? basic));
        Assert.Equal([basic.Identity], folder.Projects);

        Assert.True(solution.TryGetFolder(basic.Identity, out SolutionFolder? found));
        Assert.Same(folder, found);
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task AProjectAtTheRoot_IsInNoFolder(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        Assert.True(solution.TryGetProject(Project("WithReferences"), out ProjectSnapshot? root));
        Assert.False(solution.TryGetFolder(root.Identity, out _));
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task ASolution_ReportsItsConfigurationsAndPlatforms(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        Assert.Contains("Debug", solution.Configurations);
        Assert.Contains("Release", solution.Configurations);
        Assert.NotEmpty(solution.Platforms);
    }

    /// <summary>
    /// The project graph. A reference to a project the solution lists resolves to that project's
    /// identity, so a consumer can walk from one snapshot to another without matching paths itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task AReferenceToAProjectInTheSolution_ResolvesToItsIdentity(string file)
    {
        SolutionSnapshot solution = await OpenAsync(file);

        Assert.True(solution.TryGetProject(Project("WithReferences"), out ProjectSnapshot? referencing));

        ProjectReferenceInfo reference = Assert.Single(referencing.ProjectReferences);

        Assert.False(reference.Project.IsEmpty);
        Assert.True(solution.TryGetProject(reference.Project, out ProjectSnapshot? target));
        Assert.Equal("Basic", target.Name);
    }

    /// <summary>
    /// Opening the same project on its own establishes nothing about what else exists, so the
    /// reference stays unresolved. Same project, same reference, different answer, and both are
    /// right.
    /// </summary>
    [Fact]
    public async Task TheSameReference_IsUnresolvedWhenTheProjectIsOpenedAlone()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = Project("WithReferences"),
            },
            TestContext.Current.CancellationToken);

        ProjectSnapshot project = Assert.Single(result.Snapshot!.Projects);

        Assert.True(Assert.Single(project.ProjectReferences).Project.IsEmpty);
    }

    /// <summary>
    /// A project the solution lists and MSBuild cannot read stays in the snapshot carrying its
    /// diagnostic. Removing it would make the solution quietly smaller than it is, and would leave
    /// any folder holding it pointing at nothing.
    /// </summary>
    [Fact]
    public async Task AListedProjectThatIsNotThere_StaysInTheSnapshotWithItsDiagnostic()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = Solution("Missing.slnx"),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.SucceededWithErrors, result.Status);

        ProjectSnapshot project = Assert.Single(result.Snapshot!.Projects);

        Assert.Equal("Nowhere", project.Name);
        Assert.Equal(MSBuildDiagnosticCodes.ProjectFileNotFound, Assert.Single(project.Diagnostics).Code);

        // And the failure is visible from the result without walking the tree.
        Assert.Contains(result.Diagnostics, static d => d.Code == MSBuildDiagnosticCodes.ProjectFileNotFound);
    }

    [Fact]
    public async Task AMalformedSolution_IsADiagnosticNotAnException()
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = Solution("Broken.slnx"),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Failed, result.Status);
        Assert.Equal(MSBuildDiagnosticCodes.SolutionReadFailed, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task ThroughAWorkspace_TheWholeSolutionIsPublished(string file)
    {
        await using var workspace = new ProjectWorkspace(new MSBuildProjectProvider());

        WorkspaceLoadResult result = await workspace.LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = workspace.Identity,
                EntryPointPath = Solution(file),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Succeeded, result.Status);
        Assert.Equal(WorkspaceVersion.Initial, workspace.CurrentVersion);
        Assert.Equal(2, workspace.CurrentSnapshot!.Projects.Length);
        Assert.Single(workspace.CurrentSnapshot.Folders);

        // Every identity in the published snapshot belongs to this workspace, which is what the
        // workspace's own validation refuses to publish without.
        Assert.All(
            workspace.CurrentSnapshot.Projects,
            project => Assert.Equal(workspace.Identity, project.Identity.Workspace));
    }
}
