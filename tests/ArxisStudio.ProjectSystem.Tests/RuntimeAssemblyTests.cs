using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Working out every assembly a project needs to run.
/// </summary>
/// <remarks>
/// The question a designer has — "which files must be loadable for this project's code to run" —
/// answered against an immutable snapshot, so every case is exact and nothing is loaded, probed or
/// checked for existence.
/// </remarks>
public sealed class RuntimeAssemblyTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    [Fact]
    public void AProjectWithNothing_NeedsOnlyItsOwnOutput()
    {
        SolutionSnapshot solution = Solution(Project("App"));

        RuntimeAssemblyReference assembly = Assert.Single(solution.GetRuntimeAssemblies(Identity("App")));

        Assert.Equal(Output("App"), assembly.Path);
        Assert.Equal(RuntimeAssemblyOrigin.Project, assembly.Origin);
        Assert.Equal(Identity("App"), assembly.Project);
    }

    /// <summary>
    /// The one that separates this graph from the evaluation graph: if A references B and B
    /// references C, then C has to be there when A runs, however indirectly it got there.
    /// </summary>
    [Fact]
    public void ProjectReferences_AreFollowedThrough()
    {
        SolutionSnapshot solution = Solution(
            Project("App", references: ["Middle"]),
            Project("Middle", references: ["Leaf"]),
            Project("Leaf"));

        Assert.Equal(
            [Output("App"), Output("Middle"), Output("Leaf")],
            solution.GetRuntimeAssemblies(Identity("App")).Select(static a => a.Path));
    }

    /// <summary>
    /// The order a resolver should consult: a caller's own build wins over a copy of the same
    /// assembly arriving some other way, and a direct reference over one reached through another.
    /// </summary>
    [Fact]
    public void TheOrder_IsOwnOutputThenReferencesThenPackages()
    {
        SolutionSnapshot solution = Solution(
            Project("App", references: ["Direct"], packages: [("Serilog", "Serilog.dll")]),
            Project("Direct", references: ["Indirect"]),
            Project("Indirect"));

        Assert.Equal(
            [
                RuntimeAssemblyOrigin.Project,
                RuntimeAssemblyOrigin.ProjectReference,
                RuntimeAssemblyOrigin.ProjectReference,
                RuntimeAssemblyOrigin.Package,
            ],
            solution.GetRuntimeAssemblies(Identity("App")).Select(static a => a.Origin));
    }

    [Fact]
    public void Packages_ComeFromTheRootProjectAndCarryTheirIdentifier()
    {
        SolutionSnapshot solution = Solution(
            Project("App", packages: [("Serilog", "Serilog.dll"), ("Newtonsoft.Json", "Newtonsoft.Json.dll")]));

        ImmutableArray<RuntimeAssemblyReference> assemblies = solution.GetRuntimeAssemblies(Identity("App"));

        Assert.Equal(
            ["Serilog", "Newtonsoft.Json"],
            assemblies.Where(static a => a.Origin == RuntimeAssemblyOrigin.Package).Select(static a => a.PackageId));
    }

    /// <summary>
    /// Restore already flattened them, so the assets file of the project being asked about lists the
    /// whole transitive set. Walking to a reference's packages as well would list them twice.
    /// </summary>
    [Fact]
    public void APackageOfAReferencedProject_IsNotAddedTwice()
    {
        SolutionSnapshot solution = Solution(
            Project("App", references: ["Library"], packages: [("Serilog", "Serilog.dll")]),
            Project("Library", packages: [("Serilog", "Serilog.dll")]));

        Assert.Single(solution.GetRuntimeAssemblies(Identity("App"))
            .Where(static a => a.Origin == RuntimeAssemblyOrigin.Package));
    }

    /// <summary>
    /// A reference-assembly path would give a designer types with no method bodies, and symbols and
    /// manifests are not loadable at all.
    /// </summary>
    [Fact]
    public void OnlyTheAssemblyOutput_Counts()
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity("App"),
            Name = "App",
            ProjectFilePath = TestPaths.Project("App"),
        };

        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Output("App") });
        project.Outputs.Add(new OutputArtifact
        {
            Kind = OutputArtifactKind.ReferenceAssembly,
            Path = TestPaths.At("src", "App", "obj", "ref", "App.dll"),
        });
        project.Outputs.Add(new OutputArtifact
        {
            Kind = OutputArtifactKind.SymbolFile,
            Path = TestPaths.At("src", "App", "bin", "App.pdb"),
        });

        SolutionSnapshot solution = Solution(project.ToSnapshot());

        Assert.Equal([Output("App")], solution.GetRuntimeAssemblies(Identity("App")).Select(static a => a.Path));
    }

    /// <summary>A reference that exists only to order the build contributes nothing to run.</summary>
    [Fact]
    public void AReferenceThatContributesNoOutput_IsNotFollowed()
    {
        var app = new ProjectSnapshotBuilder
        {
            Identity = Identity("App"),
            Name = "App",
            ProjectFilePath = TestPaths.Project("App"),
        };

        app.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Output("App") });
        app.ProjectReferences.Add(new ProjectReferenceInfo
        {
            ProjectFilePath = TestPaths.Project("Tool"),
            Project = Identity("Tool"),
            ReferenceOutputAssembly = false,
        });

        SolutionSnapshot solution = Solution(app.ToSnapshot(), Project("Tool"));

        Assert.Equal([Output("App")], solution.GetRuntimeAssemblies(Identity("App")).Select(static a => a.Path));
    }

    /// <summary>A project graph should not have one, and a stack overflow is a poor way to say so.</summary>
    [Fact]
    public void ACycle_Terminates()
    {
        SolutionSnapshot solution = Solution(
            Project("App", references: ["Library"]),
            Project("Library", references: ["App"]));

        Assert.Equal(
            [Output("App"), Output("Library")],
            solution.GetRuntimeAssemblies(Identity("App")).Select(static a => a.Path));
    }

    /// <summary>
    /// A reference pointing outside the solution has no identity, and there is nothing to follow it
    /// to — the project it names was never opened.
    /// </summary>
    [Fact]
    public void AReferenceOutsideTheSnapshot_IsSkipped()
    {
        var app = new ProjectSnapshotBuilder
        {
            Identity = Identity("App"),
            Name = "App",
            ProjectFilePath = TestPaths.Project("App"),
        };

        app.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Output("App") });
        app.ProjectReferences.Add(new ProjectReferenceInfo { ProjectFilePath = TestPaths.Project("Elsewhere") });

        SolutionSnapshot solution = Solution(app.ToSnapshot());

        Assert.Single(solution.GetRuntimeAssemblies(Identity("App")));
    }

    [Fact]
    public void AProjectThatIsNotHere_HasNothing()
    {
        SolutionSnapshot solution = Solution(Project("App"));

        ImmutableArray<RuntimeAssemblyReference> assemblies =
            solution.GetRuntimeAssemblies(Identity("Absent"));

        Assert.False(assemblies.IsDefault);
        Assert.Empty(assemblies);
    }

    [Fact]
    public void AReference_DescribesItself()
    {
        SolutionSnapshot solution = Solution(Project("App", packages: [("Serilog", "Serilog.dll")]));

        Assert.Contains(
            "Package(Serilog)",
            solution.GetRuntimeAssemblies(Identity("App"))[^1].ToString(),
            System.StringComparison.Ordinal);
    }

    private static ProjectIdentity Identity(string name) =>
        ProjectIdentity.Create(Workspace, TestPaths.Project(name));

    private static CanonicalPath Output(string name) =>
        TestPaths.At("src", name, "bin", name + ".dll");

    private static ProjectSnapshot Project(
        string name,
        string[]? references = null,
        (string Package, string Assembly)[]? packages = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(name),
            Name = name,
            ProjectFilePath = TestPaths.Project(name),
        };

        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Output(name) });

        foreach (string reference in references ?? [])
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = TestPaths.Project(reference),
                Project = Identity(reference),
            });
        }

        foreach ((string package, string assembly) in packages ?? [])
        {
            project.ResolvedPackages.Add(new ResolvedPackage
            {
                PackageId = package,
                Version = "1.0.0",
                RuntimeAssemblies = [TestPaths.At("packages", package, "lib", assembly)],
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
