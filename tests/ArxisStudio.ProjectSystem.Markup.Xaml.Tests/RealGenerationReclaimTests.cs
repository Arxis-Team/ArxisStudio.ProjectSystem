using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Reclaiming a generation that has drawn real controls on a real Avalonia thread.
/// </summary>
/// <remarks>
/// <para>
/// The other reclaim tests load assemblies that never made a control; these load a project's own
/// compiled markup and let it run — a window shown and closed, a window that will not close, a control
/// subscribed to the process — because those are what a designer's generation actually meets, and
/// only Avalonia can say what Avalonia keeps.
/// </para>
/// <para>
/// Every object of the generation is made and dropped in a method of its own, not inlined: a local of
/// an asynchronous test lives as long as the test does, and would hold what the test is proving gone.
/// A weak reference is all a test keeps, to let go of what the fixture holds once the answer is in.
/// </para>
/// </remarks>
[Collection(FixtureGenerations.Name)]
public sealed class RealGenerationReclaimTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    [AvaloniaFact]
    public async Task TryReclaim_AClosedWindowOfTheProject_IsGone()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);

        ShowAndClose(generation, "FixtureWindow");

        Assert.True(
            await generation.TryReclaimAsync(TestContext.Current.CancellationToken),
            "A window of the project, shown and closed, kept its generation in the process.");
    }

    [AvaloniaFact]
    public async Task TryReclaim_AWindowThatWillNotClose_IsHeldUntilItCloses()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);
        WeakReference<Window> stubborn = ShowAndClose(generation, "StubbornWindow");

        Assert.True(stubborn.TryGetTarget(out _), "The windowing platform let go of a window that did not close.");
        Assert.False(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));

        LetItClose(stubborn);

        Assert.True(
            await generation.TryReclaimAsync(TestContext.Current.CancellationToken),
            "Asked again once the window closed, the generation stayed: closing wrote its type back into what the first answer emptied.");
    }

    [AvaloniaFact]
    public async Task TryReclaim_AControlSubscribedToTheProcess_IsHeldUntilItUnsubscribes()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);
        WeakReference<object> subscribed = Create(generation, "SubscribingControl");

        Assert.False(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));

        Unsubscribe(subscribed);

        Assert.True(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task WaitForPredecessors_AGenerationStillHeld_AnswersFalseUntilItGoes()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);
        WeakReference<object> subscribed = Create(generation, "SubscribingControl");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DesignFixtures.AssemblyName };

        Assert.False(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
        Assert.False(
            await ProjectAssemblyContext.WaitForPredecessorsAsync(names, TestContext.Current.CancellationToken),
            "An unloaded generation still in the process was not seen: AssemblyLoadContext.All does not list one that is unloading.");

        Unsubscribe(subscribed);

        Assert.True(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
        Assert.True(await ProjectAssemblyContext.WaitForPredecessorsAsync(names, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task TryReclaim_FromAThreadPoolThread_LetsTheFrameFinishOnTheDispatcher()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);

        ShowAndClose(generation, "FixtureWindow");

        Assert.True(
            await Task.Run(() => generation.TryReclaimAsync(TestContext.Current.CancellationToken).AsTask(), TestContext.Current.CancellationToken),
            "A reclaim off the user interface thread did not let the frame that drew the window finish.");
    }

    [AvaloniaFact]
    public async Task TryReclaim_AWindowShownOnceAButtonExists_IsGone()
    {
        // A button's class constructor overrides Focusable for its type, and from then on the property
        // answers every type it is asked about from a cache keyed by that type — a window of the project
        // shown on the screen is one. Any application with a button has run it; the studio always has.
        RuntimeHelpers.RunClassConstructor(typeof(Button).TypeHandle);

        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Generation(fixtures);

        ShowAndClose(generation, "FixtureWindow");

        Assert.True(
            await generation.TryReclaimAsync(TestContext.Current.CancellationToken),
            "A window of the project, shown once a button existed, kept its generation: closing, it asked a property about "
                + "its type again in the reclaim's own dispatcher turns, after the cleanup.");
    }

    [AvaloniaFact]
    public async Task TryReclaim_AGenerationThatLoadedNothingAndCompiledMarkup_IsGone()
    {
        using var fixtures = new DesignFixtures();

        // A project whose build is not there — the first design build failed — makes a generation of
        // nothing. Its documents still compile, of the controls Avalonia itself has.
        ProjectAssemblyContext generation = ProjectAssemblyContext.Create(
            fixtures.Snapshot(
                new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = fixtures.ProjectFile },
                static project => project.Outputs.Clear()),
            fixtures.Project(Workspace));

        CompileInItsScope(generation);

        Assert.True(
            await generation.TryReclaimAsync(TestContext.Current.CancellationToken),
            "A generation that loaded nothing stayed once markup was compiled in its scope: a context whose only assembly is "
                + "the compiler's dynamic one is never unloaded.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompileInItsScope(ProjectAssemblyContext generation)
    {
        using (generation.EnterLoadScope())
        {
            _ = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Load(
                "<Border xmlns='https://github.com/avaloniaui'><Button Content='Hello' /></Border>");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProjectAssemblyContext Generation(DesignFixtures fixtures) =>
        ProjectAssemblyContext.Create(fixtures.Snapshot(Workspace), fixtures.Project(Workspace));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Window> ShowAndClose(ProjectAssemblyContext generation, string name)
    {
        var window = (Window)Activator.CreateInstance(TypeOf(generation, name))!;

        window.Show();
        window.Close();

        return new WeakReference<Window>(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> Create(ProjectAssemblyContext generation, string name) =>
        new(Activator.CreateInstance(TypeOf(generation, name))!);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unsubscribe(WeakReference<object> subscribed)
    {
        if (subscribed.TryGetTarget(out object? control))
        {
            control.GetType().GetMethod("Unsubscribe")!.Invoke(control, null);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LetItClose(WeakReference<Window> stubborn)
    {
        if (stubborn.TryGetTarget(out Window? window))
        {
            window.GetType().GetProperty("AllowClose")!.SetValue(window, true);
            window.Close();
        }
    }

    private static Type TypeOf(ProjectAssemblyContext generation, string name) =>
        generation.ResolveProjectAssembly(generation.Project)!.GetType($"{DesignFixtures.Namespace}.{name}", throwOnError: true)!;
}
