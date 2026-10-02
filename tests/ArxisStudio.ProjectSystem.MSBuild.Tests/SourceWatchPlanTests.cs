using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Turning "watch what this solution is made of" into directories — without a disk, as
/// <see cref="FileWatchPlanTests"/> does for evaluation inputs.
/// </summary>
public sealed class SourceWatchPlanTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private static string Root => OperatingSystem.IsWindows() ? "C:\\" : "/";

    private static CanonicalPath At(params string[] segments) =>
        CanonicalPath.Create(Root + string.Join(System.IO.Path.DirectorySeparatorChar, segments));

    [Fact]
    public void AProjectsDirectory_IsOneWatchOfEverythingBelowIt()
    {
        // Its files, its inputs, and its restore output that is not there yet: one watch sees them
        // all, and a file moved between two of its folders arrives as a rename.
        SolutionSnapshot solution = Solution(Project(
            "App",
            inputs: [At("src", "App", "obj", "project.assets.json")],
            items: [At("src", "App", "Views", "MainWindow.axaml"), At("src", "App", "Program.cs")]));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(solution, Exists(At("src", "App"), At("src")));

        Assert.Equal([new WatchedDirectory(At("src", "App"), IncludeSubdirectories: true)], plan);
    }

    [Fact]
    public void AProjectInsideAnother_IsCoveredByIt()
    {
        SolutionSnapshot solution = Solution(
            Project("App"),
            Project(At("src", "App", "Tests", "Tests.csproj")));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(
            solution, Exists(At("src", "App"), At("src", "App", "Tests"), At("src")));

        Assert.Equal([new WatchedDirectory(At("src", "App"), IncludeSubdirectories: true)], plan);
    }

    [Fact]
    public void FilesOutsideEveryProject_AreWatchedThroughTheirDirectories()
    {
        SolutionSnapshot solution = Solution(
            At("src", "App.sln"),
            Project("App", inputs: [At("src", "Directory.Build.props")], items: [At("shared", "Common.cs")]));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(
            solution, Exists(At("src", "App"), At("src"), At("shared")));

        Assert.Equal(
            [
                new WatchedDirectory(At("src", "App"), IncludeSubdirectories: true),
                new WatchedDirectory(At("src"), IncludeSubdirectories: false),
                new WatchedDirectory(At("shared"), IncludeSubdirectories: false),
            ],
            plan);
    }

    [Fact]
    public void AMissingDirectoryAboveTheProjects_IsWatchedThroughAnAncestorThatCoversThem()
    {
        // An import whose directory is not there yet is watched through the nearest one that is,
        // with everything below it — which takes the project's own watch in.
        SolutionSnapshot solution = Solution(Project("App", inputs: [At("src", "build", "common.props")]));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(solution, Exists(At("src", "App"), At("src")));

        Assert.Equal([new WatchedDirectory(At("src"), IncludeSubdirectories: true)], plan);
    }

    [Fact]
    public void AProjectWhoseDirectoryIsGone_IsWatchedForComingBack()
    {
        SolutionSnapshot solution = Solution(Project("Gone"));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(solution, Exists(At("src")));

        Assert.Equal([new WatchedDirectory(At("src"), IncludeSubdirectories: true)], plan);
    }

    [Fact]
    public void ASolutionWithoutProjects_WatchesItsOwnDirectory()
    {
        SolutionSnapshot solution = Solution(entryPoint: At("src", "App.sln"));

        ImmutableArray<WatchedDirectory> plan = SourceWatchPlan.For(solution, Exists(At("src")));

        Assert.Equal([new WatchedDirectory(At("src"), IncludeSubdirectories: false)], plan);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SourceWatchPlan.For(null!, static _ => true));
        Assert.Throws<ArgumentNullException>(() => SourceWatchPlan.For(Solution(Project("App")), null!));
    }

    private static Func<CanonicalPath, bool> Exists(params CanonicalPath[] directories)
    {
        HashSet<CanonicalPath> existing = [.. directories];

        return existing.Contains;
    }

    private static ProjectSnapshot Project(
        string name,
        IEnumerable<CanonicalPath>? inputs = null,
        IEnumerable<CanonicalPath>? items = null) =>
        Project(At("src", name, name + ".csproj"), inputs, items);

    private static ProjectSnapshot Project(
        CanonicalPath projectFile,
        IEnumerable<CanonicalPath>? inputs = null,
        IEnumerable<CanonicalPath>? items = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(Workspace, projectFile),
            Name = projectFile.FileName,
            ProjectFilePath = projectFile,
        };

        foreach (CanonicalPath input in inputs ?? [])
        {
            project.EvaluationInputs.Add(input);
        }

        foreach (CanonicalPath item in items ?? [])
        {
            project.Items.Add(new ProjectItem { ItemType = "Compile", Include = item.FileName, FullPath = item });
        }

        return project.ToSnapshot();
    }

    private static SolutionSnapshot Solution(params ProjectSnapshot[] projects) =>
        Solution(projects.Length > 0 ? projects[0].ProjectFilePath : At("src", "App.sln"), projects);

    private static SolutionSnapshot Solution(CanonicalPath entryPoint, params ProjectSnapshot[] projects)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = entryPoint },
        };

        foreach (ProjectSnapshot project in projects)
        {
            solution.Projects.Add(project);
        }

        return solution.ToSnapshot();
    }
}
