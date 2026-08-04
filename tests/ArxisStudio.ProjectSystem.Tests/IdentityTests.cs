using System;
using System.Collections.Generic;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void WorkspaceIdentity_New_IsUniqueAndNotEmpty()
    {
        WorkspaceIdentity first = WorkspaceIdentity.New();
        WorkspaceIdentity second = WorkspaceIdentity.New();

        Assert.NotEqual(first, second);
        Assert.False(first.IsEmpty);
        Assert.True(WorkspaceIdentity.None.IsEmpty);
        Assert.Equal(default, WorkspaceIdentity.None);
    }

    [Fact]
    public void WorkspaceIdentity_ToString_IsReadable()
    {
        Assert.Equal("(none)", WorkspaceIdentity.None.ToString());
        Assert.Contains("-", WorkspaceIdentity.New().ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The determinism the specification asks for: a provider reloading the same project file in
    /// the same workspace cannot produce a different identity, because identity is derived rather
    /// than issued.
    /// </summary>
    [Fact]
    public void ProjectIdentity_ForTheSameWorkspaceAndPath_IsEqual()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();

        ProjectIdentity first = ProjectIdentity.Create(workspace, TestPaths.Project());
        ProjectIdentity second = ProjectIdentity.Create(workspace, TestPaths.Project());

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first == second);
        Assert.False(first != second);
    }

    [Fact]
    public void ProjectIdentity_DerivedFromADifferentlySpelledPath_IsStillEqual()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();

        ProjectIdentity fromSlashes = ProjectIdentity.Create(
            workspace, CanonicalPath.Create(TestPaths.Slashes("src", "App", "App.csproj")));
        ProjectIdentity fromBackslashes = ProjectIdentity.Create(
            workspace, CanonicalPath.Create(TestPaths.Backslashes("SRC", "APP", "app.csproj")));

        Assert.Equal(fromSlashes, fromBackslashes);
    }

    [Fact]
    public void ProjectIdentity_InADifferentWorkspace_IsNotEqual()
    {
        CanonicalPath path = TestPaths.Project();

        Assert.NotEqual(
            ProjectIdentity.Create(WorkspaceIdentity.New(), path),
            ProjectIdentity.Create(WorkspaceIdentity.New(), path));
    }

    [Fact]
    public void ProjectIdentity_ForADifferentProject_IsNotEqual()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();

        Assert.NotEqual(
            ProjectIdentity.Create(workspace, TestPaths.Project("App")),
            ProjectIdentity.Create(workspace, TestPaths.Project("Core")));
    }

    [Fact]
    public void ProjectIdentity_WithoutAWorkspaceOrAPath_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ProjectIdentity.Create(WorkspaceIdentity.None, TestPaths.Project()));
        Assert.Throws<ArgumentException>(
            () => ProjectIdentity.Create(WorkspaceIdentity.New(), CanonicalPath.None));
    }

    [Fact]
    public void ProjectIdentity_Default_IsNone()
    {
        Assert.True(ProjectIdentity.None.IsEmpty);
        Assert.Equal(default, ProjectIdentity.None);
        Assert.Equal("(none)", ProjectIdentity.None.ToString());
    }

    [Fact]
    public void ProjectIdentity_ToString_NamesTheProjectFile()
    {
        ProjectIdentity identity = ProjectIdentity.Create(WorkspaceIdentity.New(), TestPaths.Project());

        Assert.StartsWith("App.csproj [", identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectIdentity_WorksAsADictionaryKey()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        var map = new Dictionary<ProjectIdentity, string>
        {
            [ProjectIdentity.Create(workspace, TestPaths.Project())] = "App",
        };

        Assert.True(map.ContainsKey(ProjectIdentity.Create(workspace, TestPaths.Project())));
    }

    [Fact]
    public void ProjectIdentity_Ordering_AgreesWithEquality()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        ProjectIdentity app = ProjectIdentity.Create(workspace, TestPaths.Project("App"));
        ProjectIdentity core = ProjectIdentity.Create(workspace, TestPaths.Project("Core"));

        Assert.True(app < core);
        Assert.True(app <= core);
        Assert.True(core > app);
        Assert.True(core >= app);
        Assert.Equal(0, app.CompareTo(ProjectIdentity.Create(workspace, TestPaths.Project("App"))));
    }

    [Fact]
    public void SolutionIdentity_BehavesLikeProjectIdentity()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        CanonicalPath path = CanonicalPath.Create(TestPaths.Native("src", "App.sln"));

        SolutionIdentity first = SolutionIdentity.Create(workspace, path);
        SolutionIdentity second = SolutionIdentity.Create(workspace, path);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(SolutionIdentity.None.IsEmpty);
        Assert.StartsWith("App.sln [", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionIdentity_WithoutAWorkspaceOrAPath_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SolutionIdentity.Create(WorkspaceIdentity.None, TestPaths.Project()));
        Assert.Throws<ArgumentException>(
            () => SolutionIdentity.Create(WorkspaceIdentity.New(), CanonicalPath.None));
    }

    [Fact]
    public void WorkspaceVersion_CountsUpFromNone()
    {
        Assert.True(WorkspaceVersion.None.IsEmpty);
        Assert.Equal(default, WorkspaceVersion.None);
        Assert.Equal(WorkspaceVersion.Initial, WorkspaceVersion.None.Next());
        Assert.Equal(new WorkspaceVersion(2), WorkspaceVersion.Initial.Next());
        Assert.False(WorkspaceVersion.Initial.IsEmpty);
    }

    [Fact]
    public void WorkspaceVersion_Ordering_IsMonotonic()
    {
        WorkspaceVersion first = WorkspaceVersion.Initial;
        WorkspaceVersion second = first.Next();

        Assert.True(first < second);
        Assert.True(first <= second);
        Assert.True(second > first);
        Assert.True(second >= first);
        Assert.True(first.CompareTo(second) < 0);
    }

    [Fact]
    public void WorkspaceVersion_ToString_IsReadable()
    {
        Assert.Equal("(unpublished)", WorkspaceVersion.None.ToString());
        Assert.Equal("1", WorkspaceVersion.Initial.ToString());
    }
}
