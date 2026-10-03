using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// What a project's documents can place, by name — what the generation built and what the IDE wrote and
/// nobody built yet — and making a control placeable through the gate, never past it.
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostControlsTests
{
    private const string UsingFixtures = "using:" + DesignFixtures.Namespace;

    private const string Fresh = $$"""
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="{{DesignFixtures.Namespace}}.Fresh">
          <TextBlock Text="Fresh" />
        </UserControl>
        """;

    private const string FreshWindow = $$"""
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="{{DesignFixtures.Namespace}}.FreshWindow">
          <TextBlock Text="Fresh" />
        </Window>
        """;

    private const string FreshApplication = $$"""
        <Application xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="{{DesignFixtures.Namespace}}.FreshApplication">
        </Application>
        """;

    /// <summary>The IDE writes a control and a window that no build has produced.</summary>
    private static void WriteFresh(DesignFixtures fixtures, DesignBench bench)
    {
        fixtures.Write("Fresh.axaml", Fresh);
        fixtures.Write("FreshWindow.axaml", FreshWindow);
    }

    private static async Task<ImmutableArray<ProjectControlInfo>> ListAsync(DesignStand stand) =>
        await stand.Host.GetPlaceableControlsAsync(stand.Host.DesignSet.Single(), TestContext.Current.CancellationToken);

    [AvaloniaFact]
    public async Task GetPlaceableControlsAsync_TheBuildAndTheNewDocuments_AreControlsByNameAndNoWindow()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WriteFresh, TestContext.Current.CancellationToken);

        ImmutableArray<ProjectControlInfo> controls = await ListAsync(stand);

        // What the generation built, by name, then what it has not; the windows are not controls to place,
        // and neither is ArgumentControl: it is built, and markup cannot create it.
        Assert.Equal(["FixtureControl", "SubscribingControl", "Fresh"], controls.Select(static control => control.Name));

        ProjectControlInfo built = controls[0];

        Assert.True(built.IsBuilt);
        Assert.Equal(DesignFixtures.Namespace + ".FixtureControl", built.ClassName);
        Assert.Equal(UsingFixtures, built.XmlNamespace);
        Assert.Equal(stand.Fixtures.Document("FixtureControl.axaml"), built.Document);
        Assert.Equal(XamlTypeKinds.UserControl | XamlTypeKinds.CompiledMarkup, built.Kinds & (XamlTypeKinds.UserControl | XamlTypeKinds.CompiledMarkup));

        // A control written in code alone has no document of its own.
        Assert.True(controls[1].IsBuilt);
        Assert.True(controls[1].Document.IsEmpty);

        ProjectControlInfo fresh = controls[2];

        Assert.False(fresh.IsBuilt);
        Assert.Equal(DesignFixtures.Namespace + ".Fresh", fresh.ClassName);
        Assert.Equal(UsingFixtures, fresh.XmlNamespace);
        Assert.Equal(stand.Fixtures.Document("Fresh.axaml"), fresh.Document);
        Assert.Equal(stand.Host.DesignSet.Single(), fresh.Project);
        Assert.True(fresh.Kinds.HasFlag(XamlTypeKinds.UserControl));
    }

    [AvaloniaFact]
    public async Task GetPlaceableControlsAsync_AnApplicationsDocument_IsNotAControl()
    {
        // App.axaml declares a class like any form, and its root says it is no control to place.
        await using DesignStand stand = await DesignStand.StartAsync(
            static (fixtures, _) => fixtures.Write("FreshApplication.axaml", FreshApplication),
            TestContext.Current.CancellationToken);

        ImmutableArray<ProjectControlInfo> controls = await ListAsync(stand);

        Assert.DoesNotContain(controls, static control => control.Name == "FreshApplication");
        Assert.Equal(["FixtureControl", "SubscribingControl"], controls.Select(static control => control.Name));
    }

    [AvaloniaFact]
    public async Task GetTypeCatalogAsync_TheProjectsBuild_NamesItsDataAndItsControlsAndNoPackage()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlTypeCatalog catalog = await stand.Host.GetTypeCatalogAsync(
            stand.Host.DesignSet.Single(), TestContext.Current.CancellationToken);

        XamlTypeEntry? model = catalog.Find(DesignFixtures.Namespace + ".FixtureModel");
        XamlTypeEntry? control = catalog.Find(DesignFixtures.Namespace + ".FixtureControl");

        Assert.NotNull(model);
        Assert.Equal(XamlTypeKinds.Data, model.Kinds & XamlTypeKinds.Data);
        Assert.NotNull(control);
        Assert.Equal(XamlTypeKinds.Control, control.Kinds & XamlTypeKinds.Control);

        // Avalonia's own types are a package's, not the project's to offer.
        Assert.Null(catalog.Find("Avalonia.Controls.Button"));
    }

    [AvaloniaFact]
    public async Task GetPlaceableControlsAsync_AProjectOutsideTheDesignSet_IsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        ImmutableArray<ProjectControlInfo> controls = await stand.Host.GetPlaceableControlsAsync(
            ProjectIdentity.Create(stand.Bench.Workspace.Identity, stand.Fixtures.Document("Elsewhere.csproj")),
            TestContext.Current.CancellationToken);

        Assert.Empty(controls);
    }

    [AvaloniaFact]
    public async Task EnsureBuiltAsync_AControlTheGenerationHas_BuildsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        ProjectControlInfo control = (await ListAsync(stand)).First(static control => control.Name == "FixtureControl");
        int operations = stand.Bench.Operations.Length;

        Assert.True(await stand.Host.EnsureBuiltAsync(control, TestContext.Current.CancellationToken));
        Assert.Equal(operations, stand.Bench.Operations.Length);
    }

    [AvaloniaFact]
    public async Task EnsureBuiltAsync_AClassNoBuildProduces_IsBuiltSwappedAndStillNotPlaceable()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WriteFresh, TestContext.Current.CancellationToken);

        ProjectControlInfo fresh = (await ListAsync(stand)).Single(static control => control.Name == "Fresh");
        string? first = stand.Host.GenerationName;

        bool placeable = await stand.Host.EnsureBuiltAsync(fresh, TestContext.Current.CancellationToken);

        // Built, and the generation the build called for waited for: the answer is about the successor.
        Assert.False(placeable);
        Assert.Contains(ProjectOperationKind.Build, stand.Bench.Kinds);
        Assert.NotEqual(first, stand.Host.GenerationName);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task EnsureBuiltAsync_AFailedBuild_IsNotPlaceableAndSwapsNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WriteFresh, TestContext.Current.CancellationToken);

        ProjectControlInfo fresh = (await ListAsync(stand)).Single(static control => control.Name == "Fresh");
        string? first = stand.Host.GenerationName;

        stand.Bench.FailBuilds = true;

        Assert.False(await stand.Host.EnsureBuiltAsync(fresh, TestContext.Current.CancellationToken));
        Assert.Equal(first, stand.Host.GenerationName);
    }

    [AvaloniaFact]
    public async Task EnsureBuiltAsync_ADeferral_HoldsItUntilTheSwapItHeldOffHasRun()
    {
        await using DesignStand stand = await DesignStand.StartAsync(WriteFresh, TestContext.Current.CancellationToken);

        ProjectControlInfo fresh = (await ListAsync(stand)).Single(static control => control.Name == "Fresh");
        string? first = stand.Host.GenerationName;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        stand.Host.StateChanged += (_, e) =>
        {
            if (e.State == ProjectDesignState.SwapPending)
            {
                pending.TrySetResult();
            }
        };

        IDisposable deferral = stand.Host.Gate.Defer("a gesture on the canvas");
        Task<bool> placing = stand.Host.EnsureBuiltAsync(fresh, TestContext.Current.CancellationToken).AsTask();

        // The build is over and its types wait for the swap; whatever the host started on its own is too.
        await pending.Task.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken);
        await stand.Host.SwapInFlight.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken);

        // Built, and waiting: placing a control does not swap past somebody in the middle of something.
        Assert.False(placing.IsCompleted);
        Assert.Equal(ProjectDesignState.SwapPending, stand.Host.State);
        Assert.Equal(first, stand.Host.GenerationName);

        deferral.Dispose();

        Assert.False(await placing.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken));
        Assert.NotEqual(first, stand.Host.GenerationName);
    }
}
