using System;
using System.IO;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Pairing a project's documents with the generation's types, by <c>x:Class</c>.
/// </summary>
/// <remarks>
/// <para>
/// These tests run against the same renamed real assembly the context tests use, which holds no
/// compiled markup — so a successful pairing is answered by Markup with
/// <see cref="XamlLoaderDiagnosticCodes.NotPopulatable"/>. That is the assertion: the adapter
/// found the class in the generation and handed it over, and the refusal is Markup's honest
/// answer about a type nothing compiled markup into. Registering against a type that does carry
/// compiled markup is covered end to end in Markup's own loader tests, which own the Avalonia
/// half of the contract.
/// </para>
/// <para>
/// This test project deliberately never touches an Avalonia type, so the adapter's mapping logic
/// is exercised without a headless UI stack.
/// </para>
/// </remarks>
public sealed class ProjectXamlPopulationTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "arxis-population-" + Guid.NewGuid().ToString("N"));

    public ProjectXamlPopulationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A loaded assembly can keep its file open on some platforms, and a temporary
            // directory left behind is not a reason to fail a passing test.
        }
    }

    [Fact]
    public async Task ADocumentNamingAClassOfTheGeneration_ReachesMarkup()
    {
        (ProjectXamlPopulation population, ProjectAssemblyContext context) = Population();

        using (context)
        using (population)
        {
            XamlLivePopulationResult? result = await population.SetDocumentAsync(
                Document("ArxisStudio.ProjectSystem.CanonicalPath"),
                TestContext.Current.CancellationToken);

            // The pairing worked — the class was found in the generation's own assembly — and
            // what came back is Markup's verdict on a type without compiled markup.
            Assert.NotNull(result);
            Assert.False(result.Installed);
            Assert.Contains(
                result.Diagnostics,
                static d => d.Code == XamlLoaderDiagnosticCodes.NotPopulatable);

            Assert.Equal(0, population.Count);
        }
    }

    [Fact]
    public async Task ADocumentNamingNoClass_IsNotThisRegistrysBusiness()
    {
        (ProjectXamlPopulation population, ProjectAssemblyContext context) = Population();

        using (context)
        using (population)
        {
            XamlDocument document = XamlDocument.Parse(
                "<UserControl xmlns=\"https://github.com/avaloniaui\"><Border /></UserControl>");

            Assert.Null(await population.SetDocumentAsync(document, TestContext.Current.CancellationToken));
            Assert.False(population.RemoveDocument(document));
        }
    }

    /// <summary>
    /// A class the generation does not have is ordinary: a control added since the studio opened
    /// is ADR 0021's restart case, and a document of another project is another registry's.
    /// </summary>
    [Fact]
    public async Task ADocumentNamingAClassTheGenerationDoesNotHave_IsAnsweredWithNull()
    {
        (ProjectXamlPopulation population, ProjectAssemblyContext context) = Population();

        using (context)
        using (population)
        {
            Assert.Null(await population.SetDocumentAsync(
                Document("Some.Project.BrandNewControl"),
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AfterDisposal_RegisteringThrows()
    {
        (ProjectXamlPopulation population, ProjectAssemblyContext context) = Population();

        using (context)
        {
            population.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await population.SetDocumentAsync(
                    Document("ArxisStudio.ProjectSystem.CanonicalPath"),
                    TestContext.Current.CancellationToken));

            population.Dispose();
        }
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectXamlPopulation.Create(null!, null!));

        (ProjectXamlPopulation population, ProjectAssemblyContext context) = Population();

        using (context)
        using (population)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await population.SetDocumentAsync(null!, TestContext.Current.CancellationToken));

            Assert.Throws<ArgumentNullException>(() => population.RemoveDocument(null!));
        }
    }

    private static XamlDocument Document(string className) => XamlDocument.Parse(
        "<UserControl xmlns=\"https://github.com/avaloniaui\"\n" +
        "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
        $"             x:Class=\"{className}\">\n" +
        "    <Border />\n" +
        "</UserControl>");

    private (ProjectXamlPopulation Population, ProjectAssemblyContext Context) Population()
    {
        (XamlLoadEnvironment environment, ProjectAssemblyContext context) =
            ProjectXamlEnvironment.CreateFor(Snapshot(CopyRealAssembly("MyControls.dll")), Identity("App"));

        return (ProjectXamlPopulation.Create(context, environment), context);
    }

    private static ProjectIdentity Identity(string name) =>
        ProjectIdentity.Create(Workspace, CanonicalPath.Create(
            Path.Combine(Path.GetTempPath(), "arxis-population", name, name + ".csproj")));

    private CanonicalPath CopyRealAssembly(string name)
    {
        string source = typeof(CanonicalPath).Assembly.Location;
        string destination = Path.Combine(_root, name);

        File.Copy(source, destination, overwrite: true);

        return CanonicalPath.Create(destination);
    }

    private static SolutionSnapshot Snapshot(CanonicalPath output)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity("App"),
            Name = "App",
            ProjectFilePath = Identity("App").ProjectFilePath,
        };

        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = output });

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest
            {
                Workspace = Workspace,
                EntryPointPath = Identity("App").ProjectFilePath,
            },
        };

        solution.Projects.Add(project.ToSnapshot());

        return solution.ToSnapshot();
    }
}
