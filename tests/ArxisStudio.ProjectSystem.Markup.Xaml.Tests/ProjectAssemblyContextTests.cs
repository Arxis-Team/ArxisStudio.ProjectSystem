using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Loading a project's assemblies so a rebuilt one can be loaded again.
/// </summary>
/// <remarks>
/// <para>
/// These load real assemblies, because that is the only way to find out whether an assembly loads.
/// The one they load is a copy of this repository's own core, taken from the test output directory
/// — a real assembly, present wherever the tests run, and nothing anybody has to install.
/// </para>
/// <para>
/// It is copied rather than used in place on purpose: the interesting assertion is that loading does
/// not hold the file open, and proving that means being free to overwrite it afterwards.
/// </para>
/// </remarks>
public sealed class ProjectAssemblyContextTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "arxis-alc-" + Guid.NewGuid().ToString("N"));

    public ProjectAssemblyContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A loaded assembly can keep its file open on some platforms, and a temporary directory
            // left behind is not a reason to fail a passing test.
        }
    }

    [Fact]
    public void AProjectsOwnOutput_Loads()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        using ProjectAssemblyContext context = Context(output);

        Assembly? assembly = context.Resolve(new AssemblyName("MyControls"));

        Assert.NotNull(assembly);

        // Not an assertion about the assembly's own name: the fixture renames a file, and renaming
        // a file does not rename the assembly inside it. What matters is that it came from this
        // context rather than from the host, which is what makes it reloadable.
        Assert.NotSame(typeof(CanonicalPath).Assembly, assembly);
        Assert.True(AssemblyLoadContext.GetLoadContext(assembly)!.IsCollectible);
    }

    /// <summary>
    /// The reason the bytes are read rather than the path loaded. Holding the file open means the
    /// next build cannot write its output — so the rebuild this class exists to support would be
    /// the thing it prevented.
    /// </summary>
    [Fact]
    public void LoadingAnAssembly_DoesNotHoldItsFileOpen()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        using ProjectAssemblyContext context = Context(output);

        Assert.NotNull(context.Resolve(new AssemblyName("MyControls")));

        // What the next build does. If the file were still open this throws.
        using var rebuild = new FileStream(output.Value, FileMode.Create, FileAccess.Write, FileShare.None);

        rebuild.WriteByte(0);
    }

    /// <summary>
    /// A designer rebuilds all day, and .NET will not replace an assembly in the default context.
    /// Being collectible is what makes the next build loadable at all.
    /// </summary>
    [Fact]
    public void TheProjectsAssemblies_LoadIntoACollectibleContext()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        using ProjectAssemblyContext context = Context(output);

        Assembly assembly = context.Resolve(new AssemblyName("MyControls"))!;
        AssemblyLoadContext? loaded = AssemblyLoadContext.GetLoadContext(assembly);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, loaded);
    }

    /// <summary>
    /// Two copies of Avalonia in two contexts produce two <c>Button</c> types that are not
    /// assignable to one another, and the failure arrives much later than the mistake. Anything the
    /// host already has stays the host's.
    /// </summary>
    [Fact]
    public void SomethingTheHostAlreadyHas_StaysTheHosts()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        using ProjectAssemblyContext context = Context(output);

        // The core is loaded in the default context already, because these tests reference it.
        Assembly? core = context.Resolve(new AssemblyName("ArxisStudio.ProjectSystem"));

        Assert.NotNull(core);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(core));
        Assert.Same(typeof(CanonicalPath).Assembly, core);
    }

    [Fact]
    public void AnAssemblyNobodyMentioned_IsNotFound()
    {
        using ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        Assert.Null(context.Resolve(new AssemblyName("Nothing.Like.This")));
    }

    /// <summary>
    /// The snapshot says where a build <em>will</em> put its output; it does not promise the build
    /// ran. A missing file is a miss, not a crash.
    /// </summary>
    [Fact]
    public void AnOutputThatIsNotThere_IsAMiss()
    {
        using ProjectAssemblyContext context =
            Context(CanonicalPath.Create(Path.Combine(_root, "NeverBuilt.dll")));

        Assert.Null(context.Resolve(new AssemblyName("NeverBuilt")));
    }

    [Fact]
    public void AFileThatIsNotAnAssembly_IsAMiss()
    {
        string path = Path.Combine(_root, "Rubbish.dll");

        File.WriteAllText(path, "this is not an assembly");

        using ProjectAssemblyContext context = Context(CanonicalPath.Create(path));

        Assert.Null(context.Resolve(new AssemblyName("Rubbish")));
    }

    [Fact]
    public void AContext_NamesItselfAfterTheProjectAndTheVersion()
    {
        using ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        Assert.StartsWith("App", context.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A designer builds, loads and shows, and by the time the showing happens the model may have
    /// moved on twice. Comparing one integer is what makes the stale result rejectable.
    /// </summary>
    [Fact]
    public async Task AContext_KnowsWhichVersionItWasBuiltFrom()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        // Through a workspace, because only publication stamps a version and a hand-built pair of
        // snapshots would both carry None and compare equal whatever this did.
        await using var workspace = new ProjectWorkspace(new SnapshotProvider(output));

        CanonicalPath projectFile = Identity("App").ProjectFilePath;

        WorkspaceLoadResult loaded = await workspace.LoadAsync(
            new WorkspaceLoadRequest { Workspace = workspace.Identity, EntryPointPath = projectFile },
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceLoadStatus.Succeeded, loaded.Status);

        SolutionSnapshot first = workspace.CurrentSnapshot!;
        ProjectIdentity project = ProjectIdentity.Create(workspace.Identity, projectFile);

        using ProjectAssemblyContext context = ProjectAssemblyContext.Create(first, project);

        Assert.Equal(project, context.Project);
        Assert.True(context.IsCurrentFor(first));

        await workspace.RefreshAsync(TestContext.Current.CancellationToken);

        // Nothing about this project need have changed for the context to be worth rebuilding.
        // Rebuilding one is cheap; showing a control built from a model two refreshes old is not.
        Assert.False(context.IsCurrentFor(workspace.CurrentSnapshot!));
    }

    [Fact]
    public void ComparingAgainstNothing_Throws()
    {
        using ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        Assert.Throws<ArgumentNullException>(() => context.IsCurrentFor(null!));
    }

    [Fact]
    public void AfterDisposal_ResolvingThrows()
    {
        ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        Assert.False(context.IsUnloaded);

        context.Dispose();

        Assert.True(context.IsUnloaded);
        Assert.Throws<ObjectDisposedException>(() => context.Resolve(new AssemblyName("MyControls")));

        context.Dispose();
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProjectAssemblyContext.Create(null!, ProjectIdentity.None));

        using ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        Assert.Throws<ArgumentNullException>(() => context.Resolve(null!));
    }

    /// <summary>The environment Markup loads a document in, built from a project.</summary>
    [Fact]
    public async Task AnEnvironment_ResolvesThroughTheProject()
    {
        CanonicalPath output = CopyRealAssembly("MyControls.dll");

        (XamlLoadEnvironment environment, ProjectAssemblyContext context) =
            ProjectXamlEnvironment.CreateFor(Snapshot(output), Identity("App"));

        using (context)
        {
            Assembly? assembly = await environment.AssemblyResolver.ResolveAsync(
                new AssemblyName("MyControls"), TestContext.Current.CancellationToken);

            Assert.NotNull(assembly);
            Assert.NotNull(environment.TypeResolver);
            Assert.NotNull(environment.ResourceResolver);
            Assert.NotNull(environment.SourceProvider);
        }
    }

    /// <summary>
    /// The resolver falls through to what the process already has, which is what supplies Avalonia
    /// and everything else a document of standard controls names.
    /// </summary>
    [Fact]
    public async Task AnEnvironment_FallsBackToWhatTheProcessHasLoaded()
    {
        (XamlLoadEnvironment environment, ProjectAssemblyContext context) =
            ProjectXamlEnvironment.CreateFor(
                Snapshot(CanonicalPath.Create(Path.Combine(_root, "NeverBuilt.dll"))), Identity("App"));

        using (context)
        {
            Assembly? loader = await environment.AssemblyResolver.ResolveAsync(
                new AssemblyName("ArxisStudio.Markup.Xaml.Loader"), TestContext.Current.CancellationToken);

            Assert.NotNull(loader);
        }
    }

    [Fact]
    public async Task ACancelledResolve_Throws()
    {
        using ProjectAssemblyContext context = Context(CopyRealAssembly("MyControls.dll"));

        var resolver = new ProjectAssemblyResolver(context);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(new AssemblyName("MyControls"), cancellation.Token));
    }

    [Fact]
    public void ANullContextOrSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectAssemblyResolver(null!));
        Assert.Throws<ArgumentNullException>(() => ProjectXamlEnvironment.Create(null!));
        Assert.Throws<ArgumentNullException>(() => ProjectXamlEnvironment.CreateFor(null!, ProjectIdentity.None));
    }

    /// <summary>
    /// Publishes one project whose output is the file the test prepared.
    /// </summary>
    /// <remarks>
    /// Identities are minted from <c>request.Workspace</c> rather than from the test's own, because
    /// a workspace refuses a snapshot carrying somebody else's identity — which is the check that
    /// keeps two workspaces from sharing a project by accident, and which this fake tripped over
    /// the first time.
    /// </remarks>
    private sealed class SnapshotProvider(CanonicalPath output) : IProjectSystemProvider
    {
        public string Name => "Snapshot";

        public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

        public ValueTask<WorkspaceLoadResult> LoadAsync(
            WorkspaceLoadRequest request, CancellationToken cancellationToken)
        {
            var project = new ProjectSnapshotBuilder
            {
                Identity = ProjectIdentity.Create(request.Workspace, request.EntryPointPath),
                Name = "App",
                ProjectFilePath = request.EntryPointPath,
            };

            project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = output });

            var solution = new SolutionSnapshotBuilder
            {
                Workspace = request.Workspace,
                Name = "App",
                Request = request,
            };

            solution.Projects.Add(project.ToSnapshot());

            return ValueTask.FromResult(WorkspaceLoadResult.Success(solution.ToSnapshot()));
        }
    }

    private static ProjectIdentity Identity(string name) =>
        ProjectIdentity.Create(Workspace, CanonicalPath.Create(
            Path.Combine(Path.GetTempPath(), "arxis-alc", name, name + ".csproj")));

    /// <summary>
    /// A real assembly under a name nothing has loaded, so resolving it can only have come from
    /// this context.
    /// </summary>
    private CanonicalPath CopyRealAssembly(string name)
    {
        string source = typeof(CanonicalPath).Assembly.Location;
        string destination = Path.Combine(_root, name);

        File.Copy(source, destination, overwrite: true);

        return CanonicalPath.Create(destination);
    }

    private ProjectAssemblyContext Context(CanonicalPath output) =>
        ProjectAssemblyContext.Create(Snapshot(output), Identity("App"));

    /// <summary>
    /// A type in the project's own output, named by a bare <c>using:</c> with no assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how every <c>x:Class</c> is resolved and how Avalonia's own convention names a local
    /// control, so it is the single most important thing the environment has to be able to do — and
    /// it was the one thing it could not. The type resolver searches a list, and the adapter built
    /// one that held only the resolver: a resolver answers a name, it does not enumerate, so the
    /// project's assemblies were reachable by <c>;assembly=</c> and invisible to everything else.
    /// </para>
    /// <para>
    /// Found by running the forms designer against a real project, where every form with an
    /// <c>x:Class</c> failed with "not found in any assembly supplied to the environment" while the
    /// assembly that held the class sat in the context.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEnvironment_ResolvesATypeInTheProjectsOwnOutput_WithoutBeingToldTheAssembly()
    {
        (XamlLoadEnvironment environment, ProjectAssemblyContext context) =
            ProjectXamlEnvironment.CreateFor(Snapshot(CopyRealAssembly("MyControls.dll")), Identity("App"));

        using (context)
        {
            XamlTypeResolution resolution = await environment.TypeResolver.ResolveAsync(
                new XamlTypeName("using:ArxisStudio.ProjectSystem", nameof(CanonicalPath)),
                XamlNamespaceContext.Empty,
                TestContext.Current.CancellationToken);

            Assert.True(
                resolution.Type is not null,
                "The project's own assembly was not searched: "
                    + string.Join("; ", resolution.Diagnostics));

            // Reference equality against what the context loaded, not merely the type's name. The
            // copy carries the same assembly identity as the original this process already has
            // loaded, so a resolver that only swept the AppDomain would find a type of the right
            // name from the wrong assembly and look exactly like success.
            Assembly? loaded = context.Resolve(new AssemblyName("MyControls"));

            Assert.NotNull(loaded);
            Assert.Same(loaded, resolution.Type!.Assembly);
        }
    }

    /// <summary>
    /// The load scope puts contextual reflection on the context's own load context.
    /// </summary>
    /// <remarks>
    /// This is what makes the runtime XAML compiler's emitted code bind assembly references inside
    /// the generation that is loading, rather than in whichever context the process compiled for
    /// first. The reset half of the scope — Avalonia's own static state — is not asserted here,
    /// because reaching it would mean running a real XAML compile; the studio's end-to-end check is
    /// what covers that path.
    /// </remarks>
    [Fact]
    public void EnterLoadScope_PutsContextualReflectionOnThisGeneration()
    {
        CanonicalPath output = CopyRealAssembly("scope");

        using ProjectAssemblyContext context = ProjectAssemblyContext.Create(
            Snapshot(output), Identity("App"), "scope-test");

        Assert.Null(AssemblyLoadContext.CurrentContextualReflectionContext);

        using (context.EnterLoadScope())
        {
            Assert.Equal("scope-test", AssemblyLoadContext.CurrentContextualReflectionContext?.Name);
        }

        Assert.Null(AssemblyLoadContext.CurrentContextualReflectionContext);
    }

    [Fact]
    public void EnterLoadScope_RefusesAnUnloadedContext()
    {
        CanonicalPath output = CopyRealAssembly("scope-disposed");

        ProjectAssemblyContext context = ProjectAssemblyContext.Create(
            Snapshot(output), Identity("App"));

        context.Dispose();

        Assert.Throws<ObjectDisposedException>(() => context.EnterLoadScope());
    }

    private SolutionSnapshot Snapshot(CanonicalPath output)
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

        _ = _root;

        return solution.ToSnapshot();
    }
}
