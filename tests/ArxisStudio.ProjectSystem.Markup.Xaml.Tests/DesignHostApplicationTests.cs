using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// A form looks in the designer the way the program's application dresses it: the host loads what the
/// application document declares, as written, for each form on its own (ADR 0033).
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostApplicationTests
{
    private const string AppClass = DesignFixtures.Namespace + ".FixtureApplication";

    private static string AppDocument(string accent = "Red") =>
        "<Application xmlns=\"https://github.com/avaloniaui\"\n" +
        "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
        $"             x:Class=\"{AppClass}\"\n" +
        "             RequestedThemeVariant=\"Dark\">\n" +
        "  <Application.Styles>\n" +
        "    <Style Selector=\"Button\">\n" +
        "      <Setter Property=\"Margin\" Value=\"4\" />\n" +
        "    </Style>\n" +
        "  </Application.Styles>\n" +
        "  <Application.Resources>\n" +
        $"    <SolidColorBrush x:Key=\"Accent\" Color=\"{accent}\" />\n" +
        "  </Application.Resources>\n" +
        "</Application>";

    private static void WithApp(DesignFixtures fixtures, DesignBench bench) => fixtures.Write("App.axaml", AppDocument());

    [AvaloniaFact]
    public async Task OpenApplicationAsync_TheProjectsApplication_IsLoadedAsWrittenWithWhatItDeclares()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WithApp, TestContext.Current.CancellationToken);

        XamlLiveDocument form = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await using ProjectDesignApplication? application =
            await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        Assert.NotNull(application);
        Assert.Equal(stand.Fixtures.Document("App.axaml"), application.File);
        Assert.Equal(stand.Fixtures.Project(stand.Bench.Workspace.Identity), application.Project);

        // A plain Application: the program's App is not what the designer is dressed by, and it is not built.
        Assert.Equal(typeof(Application), application.Application!.GetType());
        Assert.Single(application.Application.Styles);
        Assert.True(application.Application.Resources.ContainsKey("Accent"));
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
        Assert.DoesNotContain(application.Diagnostics, static d => d.IsError);
    }

    [AvaloniaFact]
    public async Task OpenApplicationAsync_TwoForms_EachTakeAnApplicationOfTheirOwnOver()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WithApp, TestContext.Current.CancellationToken);

        XamlLiveDocument form = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await using ProjectDesignApplication? first = await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);
        await using ProjectDesignApplication? second = await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        Assert.NotSame(first!.Application, second!.Application);

        // Each form takes its own over, and has its own to take: a style has one owner (Markup's ClassUseTests
        // record the throw), so two forms dressed by one style would be one style with two owners.
        Assert.NotSame(first.Application!.Styles[0], second.Application!.Styles[0]);
        Assert.NotSame(first.Application.Resources, second.Application.Resources);
    }

    [AvaloniaFact]
    public async Task OpenApplicationAsync_AProjectWithNoApplication_AnswersNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument form = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        Assert.Null(await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SwapAsync_AnApplicationLeftOpen_IsClosedBeforeTheGenerationGoes()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WithApp, TestContext.Current.CancellationToken);

        XamlLiveDocument form = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        ProjectDesignApplication? application = await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.True(report.Reclaimed);
        Assert.True(application!.IsClosed);

        // The successor's is opened again, and is a new one.
        await using ProjectDesignApplication? successor = await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        Assert.NotNull(successor);
        Assert.False(successor.IsClosed);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AnOpenApplicationsDocumentSaved_IsApplicationChanged()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WithApp, TestContext.Current.CancellationToken);

        XamlLiveDocument form = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await using ProjectDesignApplication? application =
            await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        var changed = new EventProbe<ProjectDesignApplicationEventArgs>();

        stand.Host.ApplicationChanged += changed.Record;

        CanonicalPath app = stand.Fixtures.Write("App.axaml", AppDocument(accent: "Blue"));

        stand.Host.NotifyChanged([new FileChange(app, FileChangeKind.Changed)]);

        await changed.WhenCountAsync(1);

        ProjectDesignApplicationEventArgs said = Assert.Single(changed.Seen);

        Assert.Equal(app, said.File);
        Assert.Equal(application!.Project, said.Project);

        // Opened again, it says what the file says now.
        await using ProjectDesignApplication? again = await stand.Host.OpenApplicationAsync(form, TestContext.Current.CancellationToken);

        Assert.True(again!.Application!.Resources.TryGetResource("Accent", null, out object? accent));
        Assert.Equal(Colors.Blue, Assert.IsType<SolidColorBrush>(accent).Color);
    }

    [Fact]
    public void ApplicationCandidates_ALibrarysForms_AreShownWithTheApplicationsThatReferenceIt()
    {
        WorkspaceIdentity workspace = WorkspaceIdentity.New();

        SolutionSnapshot snapshot = Solution(
            workspace,
            Designed(workspace, "Viewer", "Controls"),
            Designed(workspace, "App", "Viewer"),
            Designed(workspace, "Controls"),
            Designed(workspace, "Other"));

        // Its own first, then whoever references it — through others too — in the order of the solution.
        Assert.Equal(
            [Identity(workspace, "Controls"), Identity(workspace, "Viewer"), Identity(workspace, "App")],
            ProjectDesignHost.ApplicationCandidates(snapshot, Identity(workspace, "Controls")).ToArray());

        Assert.Equal([Identity(workspace, "App")], ProjectDesignHost.ApplicationCandidates(snapshot, Identity(workspace, "App")).ToArray());
    }


    private static CanonicalPath TestPath(params string[] parts) =>
        CanonicalPath.Create(Path.Combine([Path.GetTempPath(), "arxis-applications", .. parts]));

    private static ProjectIdentity Identity(WorkspaceIdentity workspace, string name) =>
        ProjectIdentity.Create(workspace, TestPath(name, name + ".csproj"));

    /// <summary>A project with a form, referencing the projects named.</summary>
    private static ProjectSnapshot Designed(WorkspaceIdentity workspace, string name, params string[] references)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(workspace, name),
            Name = name,
            ProjectFilePath = TestPath(name, name + ".csproj"),
        };

        project.Items.Add(new ProjectItem { ItemType = "AvaloniaXaml", Include = "Form.axaml", FullPath = TestPath(name, "Form.axaml") });

        foreach (string reference in references)
        {
            project.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = TestPath(reference, reference + ".csproj"),
                Project = Identity(workspace, reference),
            });
        }

        return project.ToSnapshot();
    }

    private static SolutionSnapshot Solution(WorkspaceIdentity workspace, params ProjectSnapshot[] projects)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = workspace,
            Name = "Solution",
            Request = new WorkspaceLoadRequest { Workspace = workspace, EntryPointPath = projects[0].ProjectFilePath },
        };

        foreach (ProjectSnapshot project in projects)
        {
            solution.Projects.Add(project);
        }

        return solution.ToSnapshot();
    }
}
