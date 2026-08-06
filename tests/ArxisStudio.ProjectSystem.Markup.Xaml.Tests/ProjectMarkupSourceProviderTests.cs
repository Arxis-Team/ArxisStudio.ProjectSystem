using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Opening a document that a XAML file names by its <c>avares://</c> URI.
/// </summary>
/// <remarks>
/// The gap this closes is easy to miss: Markup's own file provider answers <c>file:</c> URIs and
/// nothing else, so following a <c>StyleInclude</c> would fail on a document sitting in the project
/// all along.
/// </remarks>
public sealed class ProjectMarkupSourceProviderTests : IDisposable
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "arxis-source-" + Guid.NewGuid().ToString("N"));

    public ProjectMarkupSourceProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ADocumentNamedByItsAvaresUri_Opens()
    {
        CanonicalPath file = Write("Views/Main.axaml", "<UserControl />");

        ProjectMarkupSourceProvider provider = Provider(file);

        MarkupSource? source = await provider.TryGetSourceAsync(
            new Uri("avares://MyApp/Views/Main.axaml"), TestContext.Current.CancellationToken);

        Assert.NotNull(source);

        // The identity stays the avares URI, because that is what the document called it and what
        // anything holding a reference to it will ask for again.
        Assert.Equal(new Uri("avares://MyApp/Views/Main.axaml"), source.Uri);

        Assert.Equal(
            "<UserControl />",
            (await source.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    /// <summary>What every editor does, and what makes a file written by one readable by the next.</summary>
    [Fact]
    public async Task AByteOrderMark_IsHonoured()
    {
        string path = Path.Combine(_root, "MyApp", "Bom.axaml");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path, "<UserControl x:Name=\"Ünïcødé\" />", new UTF8Encoding(true), TestContext.Current.CancellationToken);

        ProjectMarkupSourceProvider provider = Provider(CanonicalPath.Create(path));

        MarkupSource? source = await provider.TryGetSourceAsync(
            new Uri("avares://MyApp/Bom.axaml"), TestContext.Current.CancellationToken);

        Assert.NotNull(source);

        string text = (await source.GetTextAsync(TestContext.Current.CancellationToken)).ToString();

        Assert.Contains("Ünïcødé", text, StringComparison.Ordinal);
        Assert.DoesNotContain('﻿', text);
    }

    /// <summary>
    /// Reading is deliberately deferred: a provider asked for a source it is never read from should
    /// not have left a handle open on the file.
    /// </summary>
    [Fact]
    public async Task AskingForASource_DoesNotOpenTheFile()
    {
        CanonicalPath file = Write("Views/Main.axaml", "<UserControl />");

        ProjectMarkupSourceProvider provider = Provider(file);

        Assert.NotNull(await provider.TryGetSourceAsync(
            new Uri("avares://MyApp/Views/Main.axaml"), TestContext.Current.CancellationToken));

        // What a build does to its own output. If a handle were open this throws.
        using var rebuild = new FileStream(file.Value, FileMode.Create, FileAccess.Write, FileShare.None);

        rebuild.WriteByte((byte)'x');
    }

    /// <summary>Not knowing a URI is an ordinary answer; the composite behind it goes on asking.</summary>
    [Fact]
    public async Task AUriTheProjectDoesNotKnow_IsNotAnswered()
    {
        ProjectMarkupSourceProvider provider = Provider(Write("Views/Main.axaml", "<UserControl />"));

        Assert.Null(await provider.TryGetSourceAsync(
            new Uri("avares://MyApp/Views/Absent.axaml"), TestContext.Current.CancellationToken));

        Assert.Null(await provider.TryGetSourceAsync(
            new Uri("avares://Somebody.Else/Views/Main.axaml"), TestContext.Current.CancellationToken));
    }

    /// <summary>A file URI is the file provider's business, and it is composed behind this one.</summary>
    [Fact]
    public async Task AFileUri_IsNotThisProvidersBusiness()
    {
        CanonicalPath file = Write("Views/Main.axaml", "<UserControl />");

        ProjectMarkupSourceProvider provider = Provider(file);

        Assert.Null(await provider.TryGetSourceAsync(
            new Uri(file.Value), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADocumentWhoseFileIsGone_IsNotAnswered()
    {
        ProjectMarkupSourceProvider provider = Provider(
            CanonicalPath.Create(Path.Combine(_root, "MyApp", "Missing.axaml")));

        Assert.Null(await provider.TryGetSourceAsync(
            new Uri("avares://MyApp/Missing.axaml"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Both kinds of URI turn up in one session: a document opened by path, and one followed to by
    /// the avares URI a StyleInclude names it with.
    /// </summary>
    [Fact]
    public async Task AnEnvironment_OpensDocumentsNamedEitherWay()
    {
        CanonicalPath file = Write("Views/Main.axaml", "<UserControl />");
        SolutionSnapshot snapshot = Snapshot(file);

        (var environment, ProjectAssemblyContext context) =
            ProjectXamlEnvironment.CreateFor(snapshot, ProjectIdentity.Create(Workspace, ProjectFile));

        using (context)
        {
            Assert.NotNull(await environment.SourceProvider.TryGetSourceAsync(
                new Uri("avares://MyApp/Views/Main.axaml"), TestContext.Current.CancellationToken));

            Assert.NotNull(await environment.SourceProvider.TryGetSourceAsync(
                new Uri(file.Value), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ACancelledRequest_Throws()
    {
        ProjectMarkupSourceProvider provider = Provider(Write("Views/Main.axaml", "<UserControl />"));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.TryGetSourceAsync(new Uri("avares://MyApp/Views/Main.axaml"), cancellation.Token));
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectMarkupSourceProvider(null!));
        Assert.Throws<ArgumentNullException>(() => ProjectMarkupSourceProvider.Create(null!));

        ProjectMarkupSourceProvider provider = Provider(Write("Views/Main.axaml", "<UserControl />"));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.TryGetSourceAsync(null!, TestContext.Current.CancellationToken));
    }

    private CanonicalPath ProjectFile => CanonicalPath.Create(Path.Combine(_root, "MyApp", "MyApp.csproj"));

    private CanonicalPath Write(string relative, string content)
    {
        string path = Path.Combine(_root, "MyApp", relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return CanonicalPath.Create(path);
    }

    private ProjectMarkupSourceProvider Provider(CanonicalPath resource) =>
        ProjectMarkupSourceProvider.Create(Snapshot(resource));

    private SolutionSnapshot Snapshot(CanonicalPath resource)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(Workspace, ProjectFile),
            Name = "MyApp",
            ProjectFilePath = ProjectFile,
        };

        project.Properties["AssemblyName"] = "MyApp";
        project.Items.Add(new ProjectItem
        {
            ItemType = "AvaloniaResource",
            Include = resource.FileName,
            FullPath = resource,
        });

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "MyApp",
            Request = new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = ProjectFile },
        };

        solution.Projects.Add(project.ToSnapshot());

        return solution.ToSnapshot();
    }
}
