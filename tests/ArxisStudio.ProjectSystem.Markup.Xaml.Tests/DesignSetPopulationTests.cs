using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// The population over a design set prepares each document in the environment of the project that
/// builds its class, as that project's own documents load.
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignSetPopulationTests
{
    [AvaloniaFact]
    public async Task SetDocumentAsync_OverADesignSet_AsksForTheEnvironmentOfTheClassesProject()
    {
        using var fixtures = new DesignFixtures();

        WorkspaceIdentity workspace = WorkspaceIdentity.New();
        ProjectAssemblyContext generation = Generation(fixtures, workspace);
        var asked = new List<ProjectIdentity>();

        Assert.True(await RegisterAsync(generation, fixtures, asked), "The control's document was not installed.");
        Assert.Equal([fixtures.Project(workspace)], asked);
        Assert.True(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProjectAssemblyContext Generation(DesignFixtures fixtures, WorkspaceIdentity workspace) =>
        ProjectAssemblyContext.Create(fixtures.Snapshot(workspace), [fixtures.Project(workspace)]);

    /// <summary>Registers the fixture control's document, recording which projects' environments were asked for.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<bool> RegisterAsync(ProjectAssemblyContext generation, DesignFixtures fixtures, List<ProjectIdentity> asked)
    {
        using ProjectXamlPopulation population = ProjectXamlPopulation.Create(
            generation,
            project =>
            {
                asked.Add(project);

                return ProjectXamlEnvironment.Create(generation, project);
            });

        XamlDocument document = XamlDocument.Parse(await File.ReadAllTextAsync(fixtures.Document("FixtureControl.axaml").Value));

        return await population.SetDocumentAsync(document) is { Installed: true };
    }
}
