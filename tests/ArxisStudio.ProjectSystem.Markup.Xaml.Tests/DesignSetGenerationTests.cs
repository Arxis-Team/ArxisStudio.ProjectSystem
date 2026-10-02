using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// One generation for everything a designer shows: an application, a tool, and the library both
/// reference.
/// </summary>
/// <remarks>
/// Each project's output is a copy of a real assembly under the project's name — each a different
/// assembly, because one load context takes one assembly of an identity, and a generation of three
/// projects is three of them.
/// </remarks>
public sealed class DesignSetGenerationTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arxis-set-" + Guid.NewGuid().ToString("N"));

    public DesignSetGenerationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Create_ALibraryTwoProjectsReference_IsOneAssembly()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Tool")]);

        Assembly library = Assert.Single(generation.AssembliesOf(Id("Lib")));

        Assert.Same(library, generation.AssembliesOf(Id("App"))[1]);
        Assert.Same(library, generation.AssembliesOf(Id("Tool"))[1]);
        Assert.Single(generation.Assemblies, assembly => assembly.Path == Output("Lib"));
        Assert.Empty(generation.Diagnostics);
    }

    [Fact]
    public void AssembliesOf_AProject_IsItsOwnOutputThenWhatItReferences()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Tool")]);

        ImmutableArray<Assembly> app = generation.AssembliesOf(Id("App"));

        Assert.Equal(2, app.Length);
        Assert.Same(generation.ResolveProjectAssembly(Id("App")), app[0]);
        Assert.DoesNotContain(generation.ResolveProjectAssembly(Id("Tool")), app);
        Assert.Empty(generation.AssembliesOf(ProjectIdentity.Create(Workspace, CanonicalPath.Create(Path.Combine(_root, "Other", "Other.csproj")))));
    }

    [Fact]
    public void ResolveProjectAssembly_IsWhatTheProjectBuilds()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Tool")]);

        Assembly? app = generation.ResolveProjectAssembly(Id("App"));

        Assert.NotNull(app);
        Assert.True(AssemblyLoadContext.GetLoadContext(app)!.IsCollectible);
        Assert.Same(generation.Resolve(new AssemblyName("App")), app);
    }

    [Fact]
    public void Create_LoadsEveryAssemblyBeforeItReturns()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Tool")]);

        // What a build does next: new files under the same paths. A generation that loaded lazily
        // would take these for the library and the tool, beside the application it loaded before.
        foreach (string project in (string[])["App", "Lib", "Tool"])
        {
            File.WriteAllBytes(Output(project).Value, [1, 2, 3]);
        }

        Assert.NotNull(generation.Resolve(new AssemblyName("Lib")));
        Assert.NotNull(generation.Resolve(new AssemblyName("Tool")));
        Assert.False(generation.IsCurrentOnDisk());
    }

    [Fact]
    public void Create_TwoProjectsBuildingOneName_LoadTheFirstAndSaySo()
    {
        // The tool builds an assembly named like the application's, from a file of its own.
        SolutionSnapshot solution = Solution(toolOutput: Copy("Tool", "App", typeof(XamlLoadEnvironment)));

        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(solution, [Id("App"), Id("Tool")]);

        ProjectDiagnostic conflict = Assert.Single(generation.Diagnostics);

        Assert.Equal(ProjectDesignDiagnosticCodes.AssemblyNameConflict, conflict.Code);
        Assert.Equal(Id("Tool"), conflict.Project);
        Assert.Equal(typeof(XamlDocument).Assembly.GetName().Name, generation.Resolve(new AssemblyName("App"))!.GetName().Name);
    }

    [Fact]
    public void Create_AProjectBuildingAnAssemblyTheHostHas_IsShadowedAndSaysSo()
    {
        // The library's output is named like an assembly this process loaded: the core itself.
        string hostName = typeof(CanonicalPath).Assembly.GetName().Name!;
        SolutionSnapshot solution = Solution(libraryOutput: Copy("Lib", hostName, typeof(CanonicalPath)));

        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(solution, [Id("App")]);

        ProjectDiagnostic shadowed = Assert.Single(generation.Diagnostics);

        Assert.Equal(ProjectDesignDiagnosticCodes.ShadowedByHost, shadowed.Code);
        Assert.Same(typeof(CanonicalPath).Assembly, generation.Resolve(new AssemblyName(hostName)));
    }

    [Fact]
    public void Create_OneProject_IsASetOfOne()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), Id("App"));

        Assert.Equal([Id("App")], generation.Projects);
        Assert.Equal(Id("App"), generation.Project);
        Assert.Equal(2, generation.AssembliesOf(Id("App")).Length);
    }

    [Fact]
    public void Create_NamesItselfAfterItsProjects()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Tool")]);

        Assert.StartsWith("App+Tool @", generation.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithoutProjects_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectAssemblyContext.Create(null!, [Id("App")]));
        Assert.Throws<ArgumentNullException>(() => ProjectAssemblyContext.Create(Solution(), (IEnumerable<ProjectIdentity>)null!));
        Assert.Throws<ArgumentException>(() => ProjectAssemblyContext.Create(Solution(), [ProjectIdentity.None]));
    }

    [Fact]
    public void AfterDisposal_AskingForAProjectsAssembliesThrows()
    {
        ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App")]);

        generation.Dispose();

        Assert.Throws<ObjectDisposedException>(() => generation.AssembliesOf(Id("App")));
        Assert.Throws<ObjectDisposedException>(() => generation.ResolveProjectAssembly(Id("App")));
    }

    private ProjectIdentity Id(string name) => ProjectIdentity.Create(Workspace, ProjectFile(name));

    private CanonicalPath ProjectFile(string name) => CanonicalPath.Create(Path.Combine(_root, name, name + ".csproj"));

    private CanonicalPath Output(string name) => CanonicalPath.Create(Path.Combine(_root, name, "bin", name + ".dll"));

    /// <summary>A real assembly — the one holding <paramref name="type"/> — as a project's output of the given name.</summary>
    private CanonicalPath Copy(string project, string name, Type type)
    {
        string destination = Path.Combine(_root, project, "bin", name + ".dll");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(type.Assembly.Location, destination, overwrite: true);

        return CanonicalPath.Create(destination);
    }

    /// <summary>App and Tool, each referencing Lib.</summary>
    private SolutionSnapshot Solution(CanonicalPath toolOutput = default, CanonicalPath libraryOutput = default)
    {
        CanonicalPath app = Copy("App", "App", typeof(XamlDocument));
        CanonicalPath library = libraryOutput.IsEmpty ? Copy("Lib", "Lib", typeof(CanonicalPath)) : libraryOutput;
        CanonicalPath tool = toolOutput.IsEmpty ? Copy("Tool", "Tool", typeof(XamlLoadEnvironment)) : toolOutput;

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "Set",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = ProjectFile("App") },
        };

        solution.Projects.Add(Project("App", app, references: "Lib"));
        solution.Projects.Add(Project("Lib", library));
        solution.Projects.Add(Project("Tool", tool, references: "Lib"));

        return solution.ToSnapshot();
    }

    private ProjectSnapshot Project(string name, CanonicalPath output, string? references = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Id(name),
            Name = name,
            ProjectFilePath = ProjectFile(name),
        };

        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = output });

        if (references is not null)
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo { ProjectFilePath = ProjectFile(references), Project = Id(references) });
        }

        return project.ToSnapshot();
    }
}
