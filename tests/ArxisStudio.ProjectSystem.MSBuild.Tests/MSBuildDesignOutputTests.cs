using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Where a design build goes, asked of real evaluations — ADR 0026.
/// </summary>
/// <remarks>
/// <para>
/// Evaluations only. A build would show the same thing more directly, but an SDK project cannot be
/// built without a restore, and a restore reaches the package cache and the network, which the
/// contract keeps out of the tests. What a build does with these properties is the designer's stand's
/// to show; what the evaluation says it will do — and what the globs take — is shown here.
/// </para>
/// <para>
/// The project lives in a folder of its own with the files an IDE's Debug build leaves behind, and
/// the folder stops MSBuild's upward walk, as <c>Fixtures/Directory.Build.props</c> does.
/// </para>
/// </remarks>
public sealed class MSBuildDesignOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arxis-design-" + Guid.NewGuid().ToString("N"));

    public MSBuildDesignOutputTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(
            Path.Combine(_root, "App", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
              </PropertyGroup>
            </Project>
            """);

        Write("App", "Program.cs");

        // What the IDE's last Debug build left behind.
        Write("App", "obj", "Debug", "net10.0", "App.AssemblyInfo.cs");
        Write("App", "bin", "Debug", "net10.0", "App.deps.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // MSBuild's evaluation can hold a handle for a moment; a temporary folder left behind is
            // not a reason to fail a passing test.
        }
    }

    [Fact]
    public void GlobalProperties_SetTheOutputPathsAndLeaveTheBases()
    {
        ProjectMetadata properties = MSBuildDesignOutput.GlobalProperties;

        Assert.Equal("bin/ArxisStudio/", properties["OutputPath"]);
        Assert.Equal("obj/ArxisStudio/", properties["IntermediateOutputPath"]);
        Assert.Equal(2, properties.Count);
    }

    [Fact]
    public async Task Evaluate_WithDesignOutput_DoesNotGlobAnotherBuildsFiles()
    {
        ProjectSnapshot design = await LoadAsync(MSBuildDesignOutput.GlobalProperties);

        CanonicalPath generated = At("App", "obj", "Debug", "net10.0", "App.AssemblyInfo.cs");
        CanonicalPath output = At("App", "bin", "Debug", "net10.0", "App.deps.json");

        Assert.Contains(design.Items, item => item.FullPath == At("App", "Program.cs"));
        Assert.DoesNotContain(design.Items, item => item.FullPath == generated || item.FullPath == output);

        // Why the bases are left alone: moved, they stop excluding the IDE's output, and its generated
        // code compiles a second time.
        ProjectSnapshot moved = await LoadAsync(ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("BaseOutputPath", "bin/ArxisStudio/"),
            new KeyValuePair<string, string>("BaseIntermediateOutputPath", "obj/ArxisStudio/"),
        ]));

        Assert.Contains(moved.Items, item => item.ItemType == "Compile" && item.FullPath == generated);
        Assert.Contains(moved.Items, item => item.FullPath == output);
    }

    [Fact]
    public async Task Evaluate_WithDesignOutput_BuildsIntoItsOwnFoldersAndRestoresBesideTheIde()
    {
        ProjectSnapshot design = await LoadAsync(MSBuildDesignOutput.GlobalProperties, "PassOutputPathToReferencedProjects");

        OutputArtifact assembly = Assert.Single(design.Outputs, output => output.Kind == OutputArtifactKind.Assembly);

        Assert.Equal(At("App", "bin", "ArxisStudio", "App.dll"), assembly.Path);
        Assert.Equal(At("App", "obj", "project.assets.json"), CanonicalPath.Create(design.Properties["ProjectAssetsFile"]));
        Assert.Equal([At("App", "bin"), At("App", "obj")], design.BuildDirectories);

        // References get the output path too, unless a project turns that off — the common targets
        // remove it from them only when this is false.
        Assert.NotEqual("false", design.Properties.GetValueOrDefault("PassOutputPathToReferencedProjects"), StringComparer.OrdinalIgnoreCase);
    }

    private CanonicalPath At(params string[] segments) => CanonicalPath.Create(Path.Combine([_root, .. segments]));

    private void Write(params string[] segments)
    {
        string path = Path.Combine([_root, .. segments]);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private async Task<ProjectSnapshot> LoadAsync(ProjectMetadata globalProperties, params string[] additionalProperties)
    {
        WorkspaceLoadResult result = await new MSBuildProjectProvider().LoadAsync(
            new WorkspaceLoadRequest
            {
                Workspace = WorkspaceIdentity.New(),
                EntryPointPath = At("App", "App.csproj"),
                GlobalProperties = globalProperties,
                Options = new WorkspaceLoadOptions { AdditionalProperties = [.. additionalProperties] },
            },
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status != WorkspaceLoadStatus.Failed,
            "The load failed:\n  " + string.Join("\n  ", result.Diagnostics));

        return Assert.Single(result.Snapshot!.Projects);
    }
}
