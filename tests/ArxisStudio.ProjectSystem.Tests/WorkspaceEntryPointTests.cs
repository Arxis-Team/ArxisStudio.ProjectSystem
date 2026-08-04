using System;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class WorkspaceEntryPointTests
{
    [Theory]
    [InlineData("App.sln", WorkspaceEntryPointKind.Solution)]
    [InlineData("App.SLN", WorkspaceEntryPointKind.Solution)]
    [InlineData("App.slnx", WorkspaceEntryPointKind.SolutionXml)]
    [InlineData("App.csproj", WorkspaceEntryPointKind.Project)]
    [InlineData("App.fsproj", WorkspaceEntryPointKind.Project)]
    [InlineData("App.vbproj", WorkspaceEntryPointKind.Project)]
    [InlineData("App.vcxproj", WorkspaceEntryPointKind.Project)]
    [InlineData("App.somethingproj", WorkspaceEntryPointKind.Project)]
    [InlineData("App.txt", WorkspaceEntryPointKind.Unknown)]
    [InlineData("App", WorkspaceEntryPointKind.Unknown)]
    public void Classification_ComesFromTheExtensionAlone(string fileName, WorkspaceEntryPointKind expected)
    {
        WorkspaceEntryPoint entryPoint = WorkspaceEntryPoint.FromPath(
            CanonicalPath.Create(TestPaths.Native("src", fileName)));

        Assert.Equal(expected, entryPoint.Kind);
    }

    [Fact]
    public void FromPath_KeepsThePathAndExtension()
    {
        CanonicalPath path = CanonicalPath.Create(TestPaths.Native("src", "App.sln"));
        WorkspaceEntryPoint entryPoint = WorkspaceEntryPoint.FromPath(path);

        Assert.Equal(path, entryPoint.Path);
        Assert.Equal(".sln", entryPoint.Extension);
        Assert.False(entryPoint.IsEmpty);
    }

    [Fact]
    public void FromPath_OfNone_IsNone()
    {
        Assert.Equal(WorkspaceEntryPoint.None, WorkspaceEntryPoint.FromPath(CanonicalPath.None));
        Assert.True(WorkspaceEntryPoint.None.IsEmpty);
        Assert.Equal("(none)", WorkspaceEntryPoint.None.ToString());
    }

    [Fact]
    public void Equality_ComparesPathAndKind()
    {
        CanonicalPath path = CanonicalPath.Create(TestPaths.Native("src", "App.sln"));

        WorkspaceEntryPoint first = WorkspaceEntryPoint.FromPath(path);
        WorkspaceEntryPoint second = WorkspaceEntryPoint.FromPath(path);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals((object)second));
        Assert.False(first.Equals((object)"not an entry point"));
    }

    [Fact]
    public void ToString_NamesTheKindAndPath()
    {
        WorkspaceEntryPoint entryPoint = WorkspaceEntryPoint.FromPath(
            CanonicalPath.Create(TestPaths.Native("src", "App.sln")));

        Assert.StartsWith("Solution: ", entryPoint.ToString(), StringComparison.Ordinal);
    }
}
