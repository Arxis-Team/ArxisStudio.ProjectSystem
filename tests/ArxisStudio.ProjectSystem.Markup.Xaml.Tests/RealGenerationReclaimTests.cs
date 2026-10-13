using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        IReadOnlyCollection<CanonicalPath> built = [fixtures.Output];

        Assert.False(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
        Assert.False(
            await ProjectAssemblyContext.WaitForPredecessorsAsync(built, TestContext.Current.CancellationToken),
            "An unloaded generation still in the process was not seen: AssemblyLoadContext.All does not list one that is unloading.");

        Unsubscribe(subscribed);

        Assert.True(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));
        Assert.True(await ProjectAssemblyContext.WaitForPredecessorsAsync(built, TestContext.Current.CancellationToken));
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

        ProjectAssemblyContext generation = Nothing(fixtures);

        CompileInItsScope(generation);

        Assert.True(
            await generation.TryReclaimAsync(TestContext.Current.CancellationToken),
            "A generation that loaded nothing stayed once markup was compiled in its scope: a context whose only assembly is "
                + "the compiler's dynamic one is never unloaded.");
    }

    [AvaloniaFact]
    public async Task EnterLoadScope_AGenerationThatLoadedNothing_KeepsTheCodeItsFormsRunUntilItGoes()
    {
        using var fixtures = new DesignFixtures();

        ProjectAssemblyContext generation = Nothing(fixtures);
        WeakReference[] code = AskWhatTheFormDidNotAskAtOnce(generation);

        Assert.True(await generation.TryReclaimAsync(TestContext.Current.CancellationToken));

        Collect();

        Assert.False(
            Array.Exists(code, static trace => trace.IsAlive),
            "The code a generation's forms ran outlived the generation: what it keeps for them, it lets go of when it goes.");
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

    /// <summary>
    /// Loads a form in the generation's scope with a resource nobody asks for while it loads, collects, and asks for it
    /// then — the way a style asks the first time its control is pointed at.
    /// </summary>
    /// <returns>The code the load emitted, weakly.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] AskWhatTheFormDidNotAskAtOnce(ProjectAssemblyContext generation)
    {
        // Whatever compiled in the process earlier left the compiler there; an empty scope lets it go, so the load's code
        // is an assembly of its own.
        generation.EnterLoadScope().Dispose();

        Assembly[] before = AppDomain.CurrentDomain.GetAssemblies();
        var form = new UserControl();

        using (generation.EnterLoadScope())
        {
            // As a designer's session loads a form: into a root it made, in design mode.
            _ = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Load(
                new Avalonia.Markup.Xaml.RuntimeXamlLoaderDocument(
                    new Uri("avares://Fixture/Late.axaml"),
                    form,
                    "<UserControl xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>"
                        + "<UserControl.Resources><SolidColorBrush x:Key='Late' Color='Red' /></UserControl.Resources>"
                        + "</UserControl>"),
                new Avalonia.Markup.Xaml.RuntimeXamlLoaderConfiguration { DesignMode = true });
        }

        WeakReference[] code = EmittedSince(before);

        Assert.NotEmpty(code);

        Collect();

        // A resource nobody has asked for is a pointer to the code that builds it, and a pointer keeps no assembly alive:
        // asked once that code went, it runs freed memory and ends the process instead of failing a test.
        Assert.True(
            Array.TrueForAll(code, static trace => trace.IsAlive),
            "The code a form's markup compiled into went while the form stayed: the scope let the compiler go, and nothing "
                + "kept what it had emitted.");
        Assert.True(form.TryGetResource("Late", null, out object? brush));
        Assert.IsType<Avalonia.Media.SolidColorBrush>(brush);

        return code;
    }

    /// <summary>The collectible assemblies emitted since <paramref name="before"/>, weakly.</summary>
    /// <remarks>
    /// A method of its own, not inlined: the list of the process's assemblies it asks for holds the new ones, and a frame
    /// run once keeps every temporary of it alive until it returns.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] EmittedSince(Assembly[] before) =>
    [
        .. AppDomain.CurrentDomain.GetAssemblies()
            .Except(before)
            .Where(static assembly => assembly.IsDynamic && assembly.IsCollectible)
            .Select(static assembly => new WeakReference(assembly)),
    ];

    private static void Collect()
    {
        for (var round = 0; round < 8; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// A generation of a project whose build is not there — the first design build failed — which loads nothing. Its
    /// documents still compile, of the controls Avalonia itself has.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProjectAssemblyContext Nothing(DesignFixtures fixtures) =>
        ProjectAssemblyContext.Create(
            fixtures.Snapshot(
                new WorkspaceLoadRequest { Workspace = Workspace, EntryPointPath = fixtures.ProjectFile },
                static project => project.Outputs.Clear()),
            fixtures.Project(Workspace));

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
