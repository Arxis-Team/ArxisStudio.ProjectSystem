using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class SnapshotTests
{
    private static readonly WorkspaceIdentity Workspace = WorkspaceIdentity.New();

    [Fact]
    public void ProjectDirectory_IsDerivedFromTheProjectFile()
    {
        ProjectSnapshot project = MinimalProject();

        Assert.Equal(project.ProjectFilePath.Directory, project.ProjectDirectory);
    }

    [Fact]
    public void ABuilderMissingWhatAProjectNeeds_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ProjectSnapshotBuilder().ToSnapshot());

        Assert.Throws<InvalidOperationException>(() => new ProjectSnapshotBuilder
        {
            Identity = Identity(),
            ProjectFilePath = TestPaths.Project(),
        }.ToSnapshot());

        Assert.Throws<InvalidOperationException>(() => new ProjectSnapshotBuilder
        {
            Identity = Identity(),
            Name = "App",
        }.ToSnapshot());
    }

    /// <summary>
    /// An identity derived from one file on a snapshot describing another would make the same
    /// project appear twice in the graph, which is the failure canonical paths exist to prevent.
    /// </summary>
    [Fact]
    public void AProjectFileThatDisagreesWithItsIdentity_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new ProjectSnapshotBuilder
            {
                Identity = Identity("App"),
                Name = "App",
                ProjectFilePath = TestPaths.Project("Core"),
            }.ToSnapshot());

        Assert.Contains("appear twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MutatingTheBuilderAfterwards_DoesNotReachTheSnapshot()
    {
        ProjectSnapshotBuilder builder = Builder();
        builder.Items.Add(Item("Program.cs"));
        builder.TargetFrameworks.Add("net10.0");
        builder.Properties["OutputType"] = "Library";

        ProjectSnapshot snapshot = builder.ToSnapshot();

        builder.Items.Add(Item("Added.cs"));
        builder.TargetFrameworks.Add("net8.0");
        builder.Properties["OutputType"] = "Exe";
        builder.Properties["Added"] = "later";

        Assert.Single(snapshot.Items);
        Assert.Single(snapshot.TargetFrameworks);
        Assert.Equal("Library", snapshot.Properties["OutputType"]);
        Assert.False(snapshot.Properties.ContainsKey("Added"));
    }

    [Fact]
    public void TwoSnapshotsFromOneBuilder_ShareNothingMutable()
    {
        ProjectSnapshotBuilder builder = Builder();
        builder.Items.Add(Item("Program.cs"));

        ProjectSnapshot first = builder.ToSnapshot();
        builder.Items.Add(Item("Second.cs"));
        ProjectSnapshot second = builder.ToSnapshot();

        Assert.Single(first.Items);
        Assert.Equal(2, second.Items.Length);
    }

    /// <summary>
    /// A <c>default(ImmutableArray&lt;T&gt;)</c> throws on enumeration and is indistinguishable to a
    /// consumer from this library returning null after promising empty. Reflective on purpose: a
    /// collection added in a later milestone is covered without anyone remembering to add a case.
    /// </summary>
    [Fact]
    public void EveryCollectionOnAMinimalProjectSnapshot_IsEmptyRatherThanDefault()
    {
        AssertNoDefaultArrays(MinimalProject());
    }

    [Fact]
    public void EveryCollectionOnAMinimalSolutionSnapshot_IsEmptyRatherThanDefault()
    {
        AssertNoDefaultArrays(MinimalSolution());
    }

    [Fact]
    public void EveryCollectionOnAMinimalReference_IsEmptyRatherThanDefault()
    {
        AssertNoDefaultArrays(new ProjectReferenceInfo { ProjectFilePath = TestPaths.Project("Core") });
        AssertNoDefaultArrays(new AssemblyReferenceInfo { Name = "System.Xml" });
    }

    [Fact]
    public void AReferenceGivenADefaultArray_StoresAnEmptyOne()
    {
        var reference = new ProjectReferenceInfo
        {
            ProjectFilePath = TestPaths.Project("Core"),
            Aliases = default,
        };

        Assert.False(reference.Aliases.IsDefault);
        Assert.Empty(reference.Aliases);
    }

    [Fact]
    public void SnapshotEquality_IsReferenceEquality()
    {
        ProjectSnapshotBuilder builder = Builder();

        ProjectSnapshot first = builder.ToSnapshot();
        ProjectSnapshot second = builder.ToSnapshot();

        Assert.NotSame(first, second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, first);
    }

    [Fact]
    public void HasErrors_LooksThroughTheWholeSnapshot()
    {
        ProjectSnapshotBuilder project = Builder();
        project.Diagnostics.Add(new ProjectDiagnostic("APS1002", "broken", ProjectDiagnosticSeverity.Error));

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        solution.Projects.Add(project.ToSnapshot());

        Assert.True(solution.ToSnapshot().HasErrors);
        Assert.False(MinimalSolution().HasErrors);
    }

    [Fact]
    public void ASolutionBuilderMissingWhatItNeeds_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new SolutionSnapshotBuilder().ToSnapshot());

        Assert.Throws<InvalidOperationException>(() => new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
        }.ToSnapshot());
    }

    [Fact]
    public void TwoProjectsWithOneIdentity_Throws()
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        solution.Projects.Add(MinimalProject());
        solution.Projects.Add(MinimalProject());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(solution.ToSnapshot);

        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectFromAnotherWorkspace_Throws()
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = WorkspaceIdentity.New(),
            Name = "App",
            Request = Request(),
        };

        solution.Projects.Add(MinimalProject());

        Assert.Throws<InvalidOperationException>(solution.ToSnapshot);
    }

    [Fact]
    public void TryGetProject_FindsByIdentityAndByPath()
    {
        SolutionSnapshot solution = MinimalSolution();

        Assert.True(solution.TryGetProject(Identity(), out ProjectSnapshot? byIdentity));
        Assert.Equal("App", byIdentity.Name);

        Assert.True(solution.TryGetProject(TestPaths.Project(), out ProjectSnapshot? byPath));
        Assert.Same(byIdentity, byPath);

        Assert.False(solution.TryGetProject(TestPaths.Project("Missing"), out ProjectSnapshot? absent));
        Assert.Null(absent);
    }

    [Fact]
    public void ANewSolutionSnapshot_IsUnpublished()
    {
        Assert.Equal(WorkspaceVersion.None, MinimalSolution().Version);
    }

    [Fact]
    public void WithVersion_CopiesTheSnapshotAndSharesItsContents()
    {
        SolutionSnapshot original = MinimalSolution();
        SolutionSnapshot published = original.WithVersion(WorkspaceVersion.Initial);

        Assert.NotSame(original, published);
        Assert.Equal(WorkspaceVersion.Initial, published.Version);
        Assert.Equal(WorkspaceVersion.None, original.Version);
        Assert.Same(original.Projects[0], published.Projects[0]);
        Assert.Same(published, published.WithVersion(WorkspaceVersion.Initial));
    }

    [Fact]
    public void ASolutionOpenedOnAProject_HasNoSolutionIdentity()
    {
        Assert.True(MinimalSolution().Solution.IsEmpty);
    }

    private static void AssertNoDefaultArrays(object instance)
    {
        PropertyInfo[] arrays = [.. instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(ImmutableArray<>))];

        Assert.NotEmpty(arrays);

        foreach (PropertyInfo property in arrays)
        {
            object value = property.GetValue(instance)!;
            bool isDefault = (bool)property.PropertyType.GetProperty("IsDefault")!.GetValue(value)!;

            Assert.False(
                isDefault,
                $"{instance.GetType().Name}.{property.Name} is a default ImmutableArray, which throws on enumeration.");
        }
    }

    private static ProjectIdentity Identity(string name = "App") =>
        ProjectIdentity.Create(Workspace, TestPaths.Project(name));

    private static WorkspaceLoadRequest Request() => new()
    {
        Workspace = Workspace,
        EntryPointPath = TestPaths.Project(),
    };

    private static ProjectSnapshotBuilder Builder(string name = "App") => new()
    {
        Identity = Identity(name),
        Name = name,
        ProjectFilePath = TestPaths.Project(name),
    };

    private static ProjectSnapshot MinimalProject(string name = "App") => Builder(name).ToSnapshot();

    private static SolutionSnapshot MinimalSolution()
    {
        var builder = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = Request(),
        };

        builder.Projects.Add(MinimalProject());

        return builder.ToSnapshot();
    }

    private static ProjectItem Item(string include) => new()
    {
        ItemType = ProjectItemTypes.Compile,
        Include = include,
    };
}
