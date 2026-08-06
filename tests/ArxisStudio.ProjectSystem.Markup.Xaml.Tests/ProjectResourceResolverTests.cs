using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Resolving <c>avares://</c> to the file in the project rather than to what was last built.
/// </summary>
/// <remarks>
/// The mapping is pure and is tested that way; only the cases that actually read a file touch the
/// disk, and those write it first.
/// </remarks>
public sealed class ProjectResourceResolverTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "arxis-avares-" + Guid.NewGuid().ToString("N"));

    public ProjectResourceResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AProjectResource_ResolvesToItsFile()
    {
        CanonicalPath file = WriteResource("Views/Main.axaml", "<UserControl />");

        ProjectResourceResolver resolver = Resolver(("AvaloniaResource", file));

        XamlResource? resource = await resolver.ResolveAsync(
            new Uri("avares://MyApp/Views/Main.axaml"), null, TestContext.Current.CancellationToken);

        Assert.NotNull(resource);
        Assert.Equal(
            "<UserControl />",
            (await resource.ReadTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    /// <summary>
    /// An assembly name is case-sensitive, and <see cref="Uri"/> lowercases a host because it assumes
    /// DNS. Taking the authority from the original string is what keeps <c>MyApp</c> and <c>myapp</c>
    /// apart.
    /// </summary>
    [Fact]
    public async Task TheAssemblyName_IsCaseSensitive()
    {
        ProjectResourceResolver resolver = Resolver(("AvaloniaResource", WriteResource("Theme.axaml", "<Styles />")));

        Assert.NotNull(await resolver.ResolveAsync(
            new Uri("avares://MyApp/Theme.axaml"), null, TestContext.Current.CancellationToken));

        Assert.Null(await resolver.ResolveAsync(
            new Uri("avares://myapp/Theme.axaml"), null, TestContext.Current.CancellationToken));
    }

    /// <summary>The Avalonia SDK takes the resource path from the link when an item has one.</summary>
    [Fact]
    public async Task ALinkedItem_ResolvesUnderItsLink()
    {
        string outside = Path.Combine(_root, "shared", "Common.axaml");

        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "<Styles />", TestContext.Current.CancellationToken);

        ProjectResourceResolver resolver = Resolver(
            ("AvaloniaResource", CanonicalPath.Create(outside), "Themes/Common.axaml"));

        Assert.NotNull(await resolver.ResolveAsync(
            new Uri("avares://MyApp/Themes/Common.axaml"), null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The item type an <c>.axaml</c> document actually has. <c>AvaloniaResource</c> is what the
    /// SDK makes of an asset; <c>AvaloniaXaml</c> is what it makes of the documents a designer
    /// opens, and both are embedded under <c>avares</c>.
    /// </summary>
    /// <remarks>
    /// Mapping only <c>AvaloniaResource</c> found no documents at all in a real Avalonia project —
    /// which the sample IDE reported as zero resources for a project full of them, and which no
    /// test here noticed because every fixture used the item type the code happened to accept.
    /// </remarks>
    [Fact]
    public async Task AnAvaloniaXamlDocument_ResolvesToo()
    {
        ProjectResourceResolver resolver = Resolver(
            ("AvaloniaXaml", WriteResource("Views/Main.axaml", "<UserControl />")));

        Assert.NotNull(await resolver.ResolveAsync(
            new Uri("avares://MyApp/Views/Main.axaml"), null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Exposing every file under an avares URI would answer questions the runtime answers
    /// differently, and a designer that disagrees with the running application is worse than one
    /// that says it does not know.
    /// </summary>
    [Fact]
    public async Task AnItemThatIsNotAnAvaloniaResource_IsNotExposed()
    {
        ProjectResourceResolver resolver = Resolver(("Compile", WriteResource("Program.cs", "class P;")));

        Assert.Equal(0, resolver.Count);

        Assert.Null(await resolver.ResolveAsync(
            new Uri("avares://MyApp/Program.cs"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANonAvaresUri_IsNotThisResolversBusiness()
    {
        ProjectResourceResolver resolver = Resolver(("AvaloniaResource", WriteResource("Theme.axaml", "<Styles />")));

        Assert.Null(await resolver.ResolveAsync(
            new Uri("https://example.test/Theme.axaml"), null, TestContext.Current.CancellationToken));

        Assert.Null(await resolver.ResolveAsync(
            new Uri(Path.Combine(_root, "Theme.axaml")), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARelativeUri_IsCombinedWithItsBase()
    {
        ProjectResourceResolver resolver = Resolver(
            ("AvaloniaResource", WriteResource("Views/Main.axaml", "<UserControl />")));

        Assert.NotNull(await resolver.ResolveAsync(
            new Uri("Main.axaml", UriKind.Relative),
            new Uri("avares://MyApp/Views/Other.axaml"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The item said a file would be there and it is not. The composite behind this one goes on to
    /// ask the assemblies, which is where a package's own resource lives.
    /// </summary>
    [Fact]
    public async Task AResourceWhoseFileIsGone_IsNotResolved()
    {
        ProjectResourceResolver resolver = Resolver(
            ("AvaloniaResource", CanonicalPath.Create(Path.Combine(_root, "Missing.axaml"))));

        Assert.Null(await resolver.ResolveAsync(
            new Uri("avares://MyApp/Missing.axaml"), null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A shared theme in a control library is the ordinary case, so the map covers the whole
    /// snapshot rather than one project.
    /// </summary>
    [Fact]
    public async Task AResourceOfAnotherProject_ResolvesToo()
    {
        CanonicalPath mine = WriteResource("Views/Main.axaml", "<UserControl />");
        CanonicalPath theirs = WriteResource("Theme.axaml", "<Styles />", project: "Controls");

        SolutionSnapshot snapshot = Snapshot(
            ("MyApp", [("AvaloniaResource", mine, null)]),
            ("Controls", [("AvaloniaResource", theirs, null)]));

        ProjectResourceResolver resolver = ProjectResourceResolver.Create(snapshot);

        Assert.NotNull(await resolver.ResolveAsync(
            new Uri("avares://Controls/Theme.axaml"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledResolve_Throws()
    {
        ProjectResourceResolver resolver = Resolver(("AvaloniaResource", WriteResource("Theme.axaml", "<Styles />")));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(new Uri("avares://MyApp/Theme.axaml"), null, cancellation.Token));
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectResourceResolver.Create(null!));

        ProjectResourceResolver resolver = Resolver(("AvaloniaResource", WriteResource("Theme.axaml", "<Styles />")));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await resolver.ResolveAsync(null!, null, TestContext.Current.CancellationToken));
    }

    private CanonicalPath WriteResource(string relative, string content, string project = "MyApp")
    {
        string path = Path.Combine(_root, project, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return CanonicalPath.Create(path);
    }

    private ProjectResourceResolver Resolver(params (string ItemType, CanonicalPath Path, string? Link)[] items) =>
        ProjectResourceResolver.Create(Snapshot(("MyApp", items)));

    private ProjectResourceResolver Resolver(params (string ItemType, CanonicalPath Path)[] items) =>
        Resolver(Array.ConvertAll(items, static i => (i.ItemType, i.Path, (string?)null)));

    private SolutionSnapshot Snapshot(
        params (string Name, (string ItemType, CanonicalPath Path, string? Link)[] Items)[] projects)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest
            {
                Workspace = Workspace,
                EntryPointPath = CanonicalPath.Create(Path.Combine(_root, "App.sln")),
            },
        };

        foreach ((string name, (string ItemType, CanonicalPath Path, string? Link)[] items) in projects)
        {
            CanonicalPath projectFile = CanonicalPath.Create(Path.Combine(_root, name, name + ".csproj"));

            var project = new ProjectSnapshotBuilder
            {
                Identity = ProjectIdentity.Create(Workspace, projectFile),
                Name = name,
                ProjectFilePath = projectFile,
            };

            project.Properties["AssemblyName"] = name;

            foreach ((string itemType, CanonicalPath path, string? link) in items)
            {
                project.Items.Add(new ProjectItem
                {
                    ItemType = itemType,
                    Include = path.FileName,
                    FullPath = path,
                    Link = link,
                });
            }

            solution.Projects.Add(project.ToSnapshot());
        }

        return solution.ToSnapshot();
    }
}
