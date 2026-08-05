using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Placing a file in the project it belongs to.
/// </summary>
/// <remarks>
/// The question a tool asks the moment somebody opens a document, and the one that decides which
/// settings, references and framework apply to it.
/// </remarks>
public sealed class ProjectForFileTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    [Fact]
    public void AFileTheProjectDeclares_BelongsToIt()
    {
        SolutionSnapshot solution = Solution(Project("App", items: [TestPaths.At("src", "App", "Views", "Main.axaml")]));

        Assert.True(solution.TryGetProjectForFile(
            TestPaths.At("src", "App", "Views", "Main.axaml"), out ProjectSnapshot? project));

        Assert.Equal("App", project.Name);
    }

    /// <summary>The exact answer, and the one no amount of path comparison would reach.</summary>
    [Fact]
    public void AFileLinkedInFromOutside_BelongsToTheProjectThatLinkedIt()
    {
        SolutionSnapshot solution = Solution(
            Project("App", items: [TestPaths.At("shared", "Common.axaml")]),
            Project("Other"));

        Assert.True(solution.TryGetProjectForFile(
            TestPaths.At("shared", "Common.axaml"), out ProjectSnapshot? project));

        Assert.Equal("App", project.Name);
    }

    /// <summary>
    /// The ordinary state of a file somebody just created. Refusing to answer until the next refresh
    /// would make a new file belong to nothing.
    /// </summary>
    [Fact]
    public void AFileNothingDeclares_BelongsToTheProjectItSitsUnder()
    {
        SolutionSnapshot solution = Solution(Project("App"), Project("Other"));

        Assert.True(solution.TryGetProjectForFile(
            TestPaths.At("src", "App", "Views", "BrandNew.axaml"), out ProjectSnapshot? project));

        Assert.Equal("App", project.Name);
    }

    /// <summary>A project nested inside another's directory takes its own files.</summary>
    [Fact]
    public void ANestedProject_TakesItsOwnFiles()
    {
        var outer = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(Workspace, TestPaths.At("src", "Outer.csproj")),
            Name = "Outer",
            ProjectFilePath = TestPaths.At("src", "Outer.csproj"),
        };

        SolutionSnapshot solution = Solution(outer.ToSnapshot(), Project("App"));

        Assert.True(solution.TryGetProjectForFile(
            TestPaths.At("src", "App", "Deep", "File.axaml"), out ProjectSnapshot? project));

        Assert.Equal("App", project.Name);
    }

    /// <summary>Which items do not say, and everybody assumes.</summary>
    [Fact]
    public void AProjectFile_BelongsToItsOwnProject()
    {
        SolutionSnapshot solution = Solution(Project("App"));

        Assert.True(solution.TryGetProjectForFile(TestPaths.Project("App"), out ProjectSnapshot? project));
        Assert.Equal("App", project.Name);
    }

    [Fact]
    public void AFileNowhereNearAnything_BelongsToNothing()
    {
        SolutionSnapshot solution = Solution(Project("App"));

        Assert.False(solution.TryGetProjectForFile(
            TestPaths.At("elsewhere", "Stray.axaml"), out ProjectSnapshot? project));

        Assert.Null(project);
    }

    [Fact]
    public void AnEmptyPath_BelongsToNothing() =>
        Assert.False(Solution(Project("App")).TryGetProjectForFile(CanonicalPath.None, out _));

    private static ProjectSnapshot Project(string name, CanonicalPath[]? items = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(Workspace, TestPaths.Project(name)),
            Name = name,
            ProjectFilePath = TestPaths.Project(name),
        };

        foreach (CanonicalPath item in items ?? [])
        {
            project.Items.Add(new ProjectItem
            {
                ItemType = ProjectItemTypes.None,
                Include = item.FileName,
                FullPath = item,
            });
        }

        return project.ToSnapshot();
    }

    private static SolutionSnapshot Solution(params ProjectSnapshot[] projects)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest
            {
                Workspace = Workspace,
                EntryPointPath = TestPaths.Solution(),
            },
        };

        foreach (ProjectSnapshot project in projects)
        {
            solution.Projects.Add(project);
        }

        return solution.ToSnapshot();
    }
}
