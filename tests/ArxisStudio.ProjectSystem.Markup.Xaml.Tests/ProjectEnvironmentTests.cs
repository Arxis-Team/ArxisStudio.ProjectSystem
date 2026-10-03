using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// The environment one project's documents load in, out of a generation of the whole design set.
/// </summary>
/// <remarks>
/// The application's output is a copy of <c>ArxisStudio.Markup.Xaml</c> and the library's a copy of
/// this repository's core: two real assemblies, each holding a type the test can name.
/// </remarks>
public sealed class ProjectEnvironmentTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arxis-env-" + Guid.NewGuid().ToString("N"));

    public ProjectEnvironmentTests() => Directory.CreateDirectory(_root);

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
    public async Task Create_ForAProject_SearchesWhatItsBuildSees()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Lib")]);

        Assembly app = generation.ResolveProjectAssembly(Id("App"))!;
        Assembly library = generation.ResolveProjectAssembly(Id("Lib"))!;

        XamlLoadEnvironment forApp = ProjectXamlEnvironment.Create(generation, Id("App"));
        XamlLoadEnvironment forLibrary = ProjectXamlEnvironment.Create(generation, Id("Lib"));

        // The application sees its own types and the library's.
        Assert.Same(app, (await Resolve(forApp, "ArxisStudio.Markup.Xaml", nameof(XamlDocument)))?.Assembly);
        Assert.Same(library, (await Resolve(forApp, "ArxisStudio.ProjectSystem", nameof(CanonicalPath)))?.Assembly);

        // The library sees its own, and not the application that references it.
        Assert.Same(library, (await Resolve(forLibrary, "ArxisStudio.ProjectSystem", nameof(CanonicalPath)))?.Assembly);
        Assert.NotSame(app, (await Resolve(forLibrary, "ArxisStudio.Markup.Xaml", nameof(XamlDocument)))?.Assembly);
    }

    /// <summary>
    /// A project's closure includes the packages that map a XAML namespace of their own: a document
    /// names such a package's types by the namespace, never by the assembly.
    /// </summary>
    /// <remarks>
    /// Avalonia's themes are such packages — <c>FluentTheme</c> is written in
    /// <c>https://github.com/avaloniaui</c> and lives in <c>Avalonia.Themes.Fluent</c>. A host that
    /// happened to have the package loaded resolved the name; one that did not — an IDE with a theme of
    /// its own — answered that the namespace mapped nothing it could find, and every form lost its
    /// application's theme.
    /// </remarks>
    [Fact]
    public void AssembliesOf_IncludesThePackagesThatMapAXamlNamespace()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(package: PackageFixture()), [Id("App"), Id("Lib")]);

        Assert.Contains(generation.AssembliesOf(Id("App")), static assembly => assembly.GetName().Name == PackageAssembly);
        Assert.DoesNotContain(generation.AssembliesOf(Id("Lib")), static assembly => assembly.GetName().Name == PackageAssembly);
    }

    /// <summary>
    /// The environment built for a generation as a whole searches the packages that map a XAML
    /// namespace too, beside what the projects build.
    /// </summary>
    [Fact]
    public void Searchable_IncludesThePackagesThatMapAXamlNamespace()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(package: PackageFixture()), [Id("App")]);

        Assert.Contains(ProjectXamlEnvironment.Searchable(generation), static assembly => assembly.GetName().Name == PackageAssembly);
    }

    /// <summary>
    /// Either way of building an environment, a type written against a package's XAML namespace
    /// resolves.
    /// </summary>
    /// <remarks>
    /// What the two tests above hold, seen from a document. On its own this could not fail once the
    /// package is in the process — the type resolver looks at what is loaded too, and a package, once
    /// loaded, stays — so it is the claims above that the omissions trip.
    /// </remarks>
    [Fact]
    public async Task Create_EitherWay_ResolvesATypeByItsPackagesXamlNamespace()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(package: PackageFixture()), [Id("App")]);

        foreach (XamlLoadEnvironment environment in new[]
        {
            ProjectXamlEnvironment.Create(generation, Id("App")),
            ProjectXamlEnvironment.Create(generation),
        })
        {
            XamlTypeResolution resolution = await environment.TypeResolver.ResolveAsync(
                new XamlTypeName(PackageNamespace, "PackageGauge"),
                XamlNamespaceContext.Empty,
                TestContext.Current.CancellationToken);

            Assert.True(resolution.Success, string.Join("; ", resolution.Diagnostics));
            Assert.Equal("PackageGauge", resolution.Type!.Name);
        }
    }

    [Fact]
    public async Task Create_WithTheCurrentMap_FindsAResourceAddedSinceItWasBuilt()
    {
        string styles = Path.Combine(_root, "App", "Styles", "Extra.axaml");
        SolutionSnapshot before = Solution();
        SolutionSnapshot after = Solution(styles);
        ProjectResourceMap current = ProjectResourceMap.Create(before);

        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(before, [Id("App")]);

        XamlLoadEnvironment environment = ProjectXamlEnvironment.Create(generation, Id("App"), () => current);
        var uri = new Uri("avares://App/Styles/Extra.axaml");

        Directory.CreateDirectory(Path.GetDirectoryName(styles)!);
        await File.WriteAllTextAsync(styles, "<Styles xmlns=\"https://github.com/avaloniaui\" />", TestContext.Current.CancellationToken);

        Assert.Null(await environment.ResourceResolver.ResolveAsync(uri, null, TestContext.Current.CancellationToken));

        // The IDE added the file and the workspace read the project again: the host's current map
        // is the next snapshot's, and the environment already built sees it.
        current = ProjectResourceMap.Create(after);

        Assert.NotNull(await environment.ResourceResolver.ResolveAsync(uri, null, TestContext.Current.CancellationToken));
        Assert.NotNull(await environment.SourceProvider.TryGetSourceAsync(uri, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Create_GivenTheGenerationsMemberResolver_UsesIt()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App")]);

        var members = new XamlMemberResolver();

        Assert.Same(members, ProjectXamlEnvironment.Create(generation, Id("App"), members: members).MemberResolver);
        Assert.NotSame(members, ProjectXamlEnvironment.Create(generation, Id("App")).MemberResolver);
        Assert.Same(generation, ProjectXamlEnvironment.Create(generation, Id("App")).CompilationScope);
    }

    [Fact]
    public void CreateOptions_NamesTheProjectsOwnAssemblyAndLoadsForgivingly()
    {
        using ProjectAssemblyContext generation = ProjectAssemblyContext.Create(Solution(), [Id("App"), Id("Lib")]);

        XamlLoadOptions options = ProjectXamlEnvironment.CreateOptions(generation, Id("Lib"));

        Assert.Same(generation.ResolveProjectAssembly(Id("Lib")), options.LocalAssembly);
        Assert.Equal(XamlLoadMode.Design, options.Mode);
        Assert.False(options.UseCompiledBindingsByDefault);
        Assert.Null(options.RootAccess);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectXamlEnvironment.Create(null!, Id("App")));
        Assert.Throws<ArgumentNullException>(() => ProjectXamlEnvironment.CreateOptions(null!, Id("App")));
        Assert.Throws<ArgumentNullException>(() => new ProjectResourceResolver((Func<ProjectResourceMap>)null!));
        Assert.Throws<ArgumentNullException>(() => new ProjectMarkupSourceProvider((Func<ProjectResourceMap>)null!));
    }

    /// <summary>The XAML namespace the package fixture maps.</summary>
    private const string PackageNamespace = "https://github.com/arxis-team/projectsystem/package-fixtures";

    /// <summary>The package fixture's assembly name.</summary>
    private const string PackageAssembly = "ArxisStudio.ProjectSystem.Markup.Xaml.PackageFixtures";

    private static async Task<Type?> Resolve(XamlLoadEnvironment environment, string ns, string name) =>
        (await environment.TypeResolver.ResolveAsync(
            new XamlTypeName("using:" + ns, name),
            XamlNamespaceContext.Empty,
            TestContext.Current.CancellationToken)).Type;

    private ProjectIdentity Id(string name) => ProjectIdentity.Create(Workspace, ProjectFile(name));

    private CanonicalPath ProjectFile(string name) => CanonicalPath.Create(Path.Combine(_root, name, name + ".csproj"));

    private CanonicalPath Copy(string project, Type type)
    {
        string destination = Path.Combine(_root, project, "bin", project + ".dll");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(type.Assembly.Location, destination, overwrite: true);

        return CanonicalPath.Create(destination);
    }

    /// <summary>
    /// The package fixture where the tests' build put it. Not copied into the test's folder: a package is
    /// loaded from its path into the default context and holds the file for the life of the process.
    /// </summary>
    private static CanonicalPath PackageFixture() =>
        CanonicalPath.Create(Path.Combine(AppContext.BaseDirectory, "fixtures", "package", PackageAssembly + ".dll"));

    /// <summary>
    /// App referencing Lib; with a style sheet among the app's items when one is named, and a restored
    /// package's runtime asset when one is.
    /// </summary>
    private SolutionSnapshot Solution(string? styles = null, CanonicalPath? package = null)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "Env",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = ProjectFile("App") },
        };

        var app = new ProjectSnapshotBuilder { Identity = Id("App"), Name = "App", ProjectFilePath = ProjectFile("App") };

        app.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Copy("App", typeof(XamlDocument)) });
        app.ProjectReferences.Add(new ProjectReferenceInfo { ProjectFilePath = ProjectFile("Lib"), Project = Id("Lib") });
        app.Properties["AssemblyName"] = "App";

        if (styles is not null)
        {
            app.Items.Add(new ProjectItem
            {
                ItemType = "AvaloniaXaml",
                Include = "Styles/Extra.axaml",
                FullPath = CanonicalPath.Create(styles),
            });
        }

        if (package is { } runtime)
        {
            app.ResolvedPackages.Add(new ResolvedPackage
            {
                PackageId = "Fixtures.Package",
                Version = "1.0.0",
                RuntimeAssemblies = [runtime],
                IsDirect = true,
            });
        }

        var library = new ProjectSnapshotBuilder { Identity = Id("Lib"), Name = "Lib", ProjectFilePath = ProjectFile("Lib") };

        library.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Copy("Lib", typeof(CanonicalPath)) });

        solution.Projects.Add(app.ToSnapshot());
        solution.Projects.Add(library.ToSnapshot());

        return solution.ToSnapshot();
    }
}
