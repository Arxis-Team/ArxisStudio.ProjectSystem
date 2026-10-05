using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// When the generation itself is outlived: the model changed what it is made of, a package it loaded
/// moved to another file, or another generation of the same assemblies — or of the same types — is still
/// in the process.
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostGenerationTests
{
    /// <summary>What a test holds of a generation, for as long as it means to.</summary>
    private static object? _held;

    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    [AvaloniaFact]
    public async Task RefreshAsync_AProjectJoinsTheDesignSet_SwapsTheGeneration()
    {
        var joined = false;

        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, bench) => bench.Snapshot = request => fixtures.Snapshot(
                request,
                more: workspace => joined ? [Library(fixtures, workspace)] : []),
            TestContext.Current.CancellationToken);

        var swaps = new EventProbe<ProjectDesignSwapCompletedEventArgs>();

        stand.Host.SwapCompleted += swaps.Record;

        Assert.Single(stand.Host.DesignSet);

        joined = true;

        await stand.Bench.Workspace.RefreshAsync(TestContext.Current.CancellationToken);
        await swaps.WhenCountAsync(1);

        Assert.True(swaps.Seen[0].Report.Reclaimed);
        Assert.Equal(2, stand.Host.DesignSet.Length);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_APackageTheGenerationLoadedMovesToAnotherFile_RequiresARestart()
    {
        string name = "Packaged" + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "arxis-package-" + Guid.NewGuid().ToString("N"));
        CanonicalPath first = Package(Path.Combine(folder, "1.0.0"), name);
        CanonicalPath second = Package(Path.Combine(folder, "2.0.0"), name);
        CanonicalPath package = first;

        try
        {
            await using DesignStand stand = await DesignStand.StartAsync(
                (fixtures, bench) =>
                {
                    fixtures.Write("Packaged.axaml", $"""
                        <UserControl xmlns="https://github.com/avaloniaui"
                                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                     xmlns:pkg="clr-namespace:Packaged;assembly={name}">
                          <UserControl.Resources>
                            <pkg:Thing x:Key="thing" />
                          </UserControl.Resources>
                        </UserControl>
                        """);

                    bench.Snapshot = request => fixtures.Snapshot(
                        request,
                        project => project.ResolvedPackages.Add(new ResolvedPackage
                        {
                            PackageId = name,
                            Version = package == first ? "1.0.0" : "2.0.0",
                            RuntimeAssemblies = [package],
                        }));
                },
                TestContext.Current.CancellationToken);

            await stand.OpenAsync("Packaged.axaml", TestContext.Current.CancellationToken);

            var restarts = new EventProbe<ProjectDesignRestartEventArgs>();

            stand.Host.RestartRequired += restarts.Record;

            package = second;

            await stand.Bench.Workspace.RefreshAsync(TestContext.Current.CancellationToken);
            await restarts.WhenCountAsync(1);

            Assert.Equal(ProjectDesignRestartReason.PackagesChanged, restarts.Seen[0].Reason);
            Assert.Contains(name, restarts.Seen[0].Message, StringComparison.Ordinal);
            Assert.Equal(ProjectDesignState.RestartRequired, stand.Host.State);
            Assert.NotNull(stand.Host.GenerationName);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Loaded into the process for the rest of its life, which is the point of the test.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [AvaloniaFact]
    public async Task StartAsync_AnotherGenerationOfTheAssembliesStillHeld_RequiresARestart()
    {
        using var held = new DesignFixtures();

        ProjectAssemblyContext predecessor = Generation(held);
        WeakReference<object> subscribed = Subscribe(predecessor);

        try
        {
            Assert.False(await predecessor.TryReclaimAsync(TestContext.Current.CancellationToken));

            await using DesignStand stand = await DesignStand.StartAsync(
                (_, _) => { },
                TestContext.Current.CancellationToken);

            Assert.Equal(ProjectDesignState.RestartRequired, stand.Host.State);
            Assert.Null(stand.Host.GenerationName);

            XamlLiveDocument document = await stand.Host.OpenDocumentAsync(
                stand.Fixtures.Document("FixtureControl.axaml"), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(XamlLiveDocumentState.Detached, document.State);
        }
        finally
        {
            Unsubscribe(subscribed);

            Assert.True(await predecessor.TryReclaimAsync(TestContext.Current.CancellationToken));
        }
    }

    [AvaloniaFact]
    public async Task StartAsync_AGenerationOfOtherAssembliesWithTheSameTypesStillHeld_RequiresARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "arxis-namesake-" + Guid.NewGuid().ToString("N"));
        ProjectAssemblyContext predecessor = Namesake(folder);

        Hold(predecessor);

        try
        {
            Assert.False(await predecessor.TryReclaimAsync(TestContext.Current.CancellationToken));

            await using DesignStand stand = await DesignStand.StartAsync(
                (_, _) => { },
                TestContext.Current.CancellationToken);

            Assert.Equal(ProjectDesignState.RestartRequired, stand.Host.State);
            Assert.Null(stand.Host.GenerationName);
        }
        finally
        {
            _held = null;

            Assert.True(await predecessor.TryReclaimAsync(TestContext.Current.CancellationToken));

            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A library project that joins the solution, unbuilt: its output is not there.</summary>
    private static ProjectSnapshot Library(DesignFixtures fixtures, WorkspaceIdentity workspace)
    {
        CanonicalPath projectFile = CanonicalPath.Create(Path.Combine(fixtures.Root, "Lib", "Lib.csproj"));

        var library = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, projectFile),
            Name = "Lib",
            ProjectFilePath = projectFile,
        };

        library.Outputs.Add(new OutputArtifact
        {
            Kind = OutputArtifactKind.Assembly,
            Path = CanonicalPath.Create(Path.Combine(fixtures.Root, "Lib", "bin", "Lib.dll")),
        });

        library.Items.Add(new ProjectItem
        {
            ItemType = "AvaloniaXaml",
            Include = "Panel.axaml",
            FullPath = CanonicalPath.Create(Path.Combine(fixtures.Root, "Lib", "Panel.axaml")),
        });

        return library.ToSnapshot();
    }

    /// <summary>Writes an assembly no process has loaded, with a type a document can make: a package's.</summary>
    private static CanonicalPath Package(string folder, string name)
    {
        Directory.CreateDirectory(folder);

        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule(name);
        TypeBuilder thing = module.DefineType("Packaged.Thing", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

        thing.DefineDefaultConstructor(MethodAttributes.Public);
        thing.CreateType();

        string path = Path.Combine(folder, name + ".dll");

        assembly.Save(path);

        return CanonicalPath.Create(path);
    }

    /// <summary>
    /// A generation of another project, whose build declares one of the fixtures' types under an assembly
    /// name of its own — a renamed copy of the project, as far as a document's <c>using:</c> can tell.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProjectAssemblyContext Namesake(string folder)
    {
        string name = "Namesake" + Guid.NewGuid().ToString("N");
        string output = Path.Combine(folder, "bin", name + ".dll");
        CanonicalPath projectFile = CanonicalPath.Create(Path.Combine(folder, "Namesake.csproj"));

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        TypeBuilder control = assembly.DefineDynamicModule(name).DefineType(
            $"{DesignFixtures.Namespace}.FixtureControl", TypeAttributes.Public | TypeAttributes.Class);

        control.DefineDefaultConstructor(MethodAttributes.Public);
        control.CreateType();
        assembly.Save(output);

        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(Workspace, projectFile),
            Name = "Namesake",
            ProjectFilePath = projectFile,
        };

        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = CanonicalPath.Create(output) });

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "Namesake",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = projectFile },
        };

        solution.Projects.Add(project.ToSnapshot());

        return ProjectAssemblyContext.Create(solution.ToSnapshot(), project.Identity);
    }

    /// <summary>Holds an instance of the generation's type that has a fixture's name, until the test lets go.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Hold(ProjectAssemblyContext generation) =>
        _held = Activator.CreateInstance(generation.ResolveProjectAssembly(generation.Project)!
            .GetType($"{DesignFixtures.Namespace}.FixtureControl", throwOnError: true)!);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProjectAssemblyContext Generation(DesignFixtures fixtures) =>
        ProjectAssemblyContext.Create(fixtures.Snapshot(Workspace), fixtures.Project(Workspace));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> Subscribe(ProjectAssemblyContext generation) =>
        new(Activator.CreateInstance(generation.ResolveProjectAssembly(generation.Project)!
            .GetType($"{DesignFixtures.Namespace}.SubscribingControl", throwOnError: true)!)!);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unsubscribe(WeakReference<object> subscribed)
    {
        if (subscribed.TryGetTarget(out object? control))
        {
            control.GetType().GetMethod("Unsubscribe")!.Invoke(control, null);
        }
    }
}
