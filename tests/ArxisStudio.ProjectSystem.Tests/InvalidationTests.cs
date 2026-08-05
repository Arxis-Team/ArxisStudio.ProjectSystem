using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Turning "these files changed" into "this much is stale".
/// </summary>
/// <remarks>
/// Pure over an immutable snapshot, so every case here is exact and none of them touches a disk, a
/// timer or a thread. That is the point of computing invalidation against a snapshot rather than
/// deciding it inside a watcher.
/// </remarks>
public sealed class InvalidationTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private static CanonicalPath Shared => TestPaths.At("src", "Directory.Build.props");

    [Fact]
    public void AChangeNothingDependsOn_InvalidatesNothing()
    {
        WorkspaceInvalidation invalidation = Solution().Invalidate([TestPaths.At("src", "App", "Program.cs")]);

        Assert.Equal(WorkspaceInvalidationScope.None, invalidation.Scope);
        Assert.True(invalidation.IsEmpty);
        Assert.Empty(invalidation.Projects);
        Assert.Empty(invalidation.Causes);
        Assert.Equal("None", invalidation.ToString());
    }

    [Fact]
    public void NoChangesAtAll_InvalidateNothing()
    {
        Assert.Same(WorkspaceInvalidation.None, Solution().Invalidate([]));
        Assert.Same(WorkspaceInvalidation.None, Solution().Invalidate([CanonicalPath.None]));
    }

    [Fact]
    public void ANullArgument_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Solution().Invalidate(null!));

    [Fact]
    public void AChangedProjectFile_InvalidatesThatProjectAlone()
    {
        SolutionSnapshot solution = Solution();

        WorkspaceInvalidation invalidation = solution.Invalidate([TestPaths.Project("App")]);

        Assert.Equal(WorkspaceInvalidationScope.Projects, invalidation.Scope);
        Assert.False(invalidation.IsEmpty);
        Assert.Equal([Identity("App")], invalidation.Projects);
        Assert.Equal([TestPaths.Project("App")], invalidation.Causes);
    }

    /// <summary>
    /// The many-to-one case that makes this worth computing rather than assuming: one file is an
    /// input of every project, so touching it stales all of them at once.
    /// </summary>
    [Fact]
    public void AChangedSharedImport_InvalidatesEveryProjectThatImportsIt()
    {
        WorkspaceInvalidation invalidation = Solution().Invalidate([Shared]);

        Assert.Equal([Identity("App"), Identity("Library")], invalidation.Projects);
        Assert.Equal([Shared], invalidation.Causes);
    }

    [Fact]
    public void SeveralChanges_AreOneAnswer()
    {
        WorkspaceInvalidation invalidation = Solution().Invalidate(
            [TestPaths.At("src", "App", "Program.cs"), TestPaths.Project("Library"), Shared]);

        Assert.Equal([Identity("App"), Identity("Library")], invalidation.Projects);

        // The source file is dropped and the order of what is left is the order it arrived in.
        Assert.Equal([TestPaths.Project("Library"), Shared], invalidation.Causes);
    }

    [Fact]
    public void TheSameChangeTwice_IsCountedOnce()
    {
        WorkspaceInvalidation invalidation = Solution().Invalidate([Shared, Shared, Shared]);

        Assert.Equal(2, invalidation.Projects.Length);
        Assert.Equal([Shared], invalidation.Causes);
    }

    /// <summary>
    /// A solution that changed may have gained or lost projects, so "which of the current ones are
    /// stale" stops being the question.
    /// </summary>
    [Fact]
    public void AChangedSolutionFile_InvalidatesTheEntryPoint()
    {
        SolutionSnapshot solution = Solution(entryPoint: TestPaths.Solution());

        WorkspaceInvalidation invalidation = solution.Invalidate([TestPaths.Solution(), Shared]);

        Assert.Equal(WorkspaceInvalidationScope.EntryPoint, invalidation.Scope);
        Assert.Equal([TestPaths.Solution()], invalidation.Causes);

        // Deliberately not the projects that happen to be open now: what will be open afterwards is
        // not known until the solution has been read again.
        Assert.Empty(invalidation.Projects);
    }

    /// <summary>
    /// A project file cannot add a project to a workspace, so a standalone entry point is stale like
    /// any other project rather than reopening the workspace.
    /// </summary>
    [Fact]
    public void AChangedStandaloneProject_IsNotAnEntryPointInvalidation()
    {
        SolutionSnapshot solution = Solution();

        WorkspaceInvalidation invalidation = solution.Invalidate([TestPaths.Project("App")]);

        Assert.Equal(WorkspaceInvalidationScope.Projects, invalidation.Scope);
    }

    /// <summary>
    /// This looks like a missing feature and is a deliberate finding: evaluating a project does not
    /// read the projects it references. MSBuild resolves a project reference during a build, so a
    /// change to the referenced project leaves the referencing snapshot correct in every field, and
    /// re-evaluating it would produce exactly what it already said.
    /// </summary>
    [Fact]
    public void AChangedReferencedProject_DoesNotInvalidateWhatReferencesIt()
    {
        WorkspaceInvalidation invalidation = Solution().Invalidate([TestPaths.Project("Library")]);

        Assert.Equal([Identity("Library")], invalidation.Projects);
        Assert.DoesNotContain(Identity("App"), invalidation.Projects);
    }

    [Fact]
    public void AnInvalidation_DescribesItself()
    {
        Assert.Equal(
            "Projects: 2 stale projects, caused by 1 change",
            Solution().Invalidate([Shared]).ToString());

        Assert.Equal(
            "Projects: 1 stale project, caused by 1 change",
            Solution().Invalidate([TestPaths.Project("App")]).ToString());

        Assert.Equal(
            "EntryPoint, caused by 1 change",
            Solution(entryPoint: TestPaths.Solution()).Invalidate([TestPaths.Solution()]).ToString());
    }

    private static ProjectIdentity Identity(string name) =>
        ProjectIdentity.Create(Workspace, TestPaths.Project(name));

    /// <summary>
    /// Two projects that share an import, one referencing the other, so every rule above has
    /// something to distinguish.
    /// </summary>
    private static SolutionSnapshot Solution(CanonicalPath entryPoint = default)
    {
        CanonicalPath path = entryPoint.IsEmpty ? TestPaths.Project("App") : entryPoint;

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = path },
        };

        solution.Projects.Add(Project("App", references: "Library"));
        solution.Projects.Add(Project("Library"));

        return solution.ToSnapshot();
    }

    private static ProjectSnapshot Project(string name, string? references = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(name),
            Name = name,
            ProjectFilePath = TestPaths.Project(name),
        };

        project.EvaluationInputs.Add(TestPaths.Project(name));
        project.EvaluationInputs.Add(Shared);

        if (references is not null)
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = TestPaths.Project(references),
                Project = Identity(references),
            });
        }

        return project.ToSnapshot();
    }
}
