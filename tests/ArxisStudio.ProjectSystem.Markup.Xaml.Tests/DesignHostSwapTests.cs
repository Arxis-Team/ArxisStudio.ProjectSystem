using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Replacing a generation under open documents: in order, only when nothing holds it off, and never
/// beside a generation that would not go.
/// </summary>
/// <remarks>
/// A test keeps names and weak references of what a generation built, never the objects: a local of an
/// asynchronous test lives as long as the test does, and would hold what the swap is proving gone.
/// </remarks>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostSwapTests
{
    private const string Holder = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:local="using:ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures">
          <local:SubscribingControl />
        </UserControl>
        """;

    [AvaloniaFact]
    public async Task BuildAsync_ARewrittenOutput_SwapsTheGenerationUnderTheDocument()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        string? first = stand.Host.GenerationName;

        Assert.Equal(first, DesignStand.GenerationOf(document));

        var swaps = new EventProbe<ProjectDesignSwapCompletedEventArgs>();
        var statesSeen = new System.Collections.Generic.List<ProjectDesignState>();

        stand.Host.SwapCompleted += swaps.Record;
        stand.Host.SwapCompleted += (_, _) => statesSeen.Add(stand.Host.State);

        ProjectDesignBuildResult built = await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(built.TypesChanged);

        await swaps.WhenCountAsync(1);

        ProjectDesignSwapReport report = swaps.Seen[0].Report;

        Assert.True(report.Reclaimed);
        Assert.Equal(first, report.Generation);
        Assert.NotEqual(first, report.Successor);
        Assert.Equal(report.Successor, stand.Host.GenerationName);
        Assert.Equal(report.Successor, DesignStand.GenerationOf(document));
        Assert.Equal(XamlLiveDocumentState.Live, document.State);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
        Assert.Equal([ProjectDesignState.Live], statesSeen);
    }

    [AvaloniaFact]
    public async Task Gate_ADeferral_HoldsTheSwapUntilItLetsGo()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        var swaps = new EventProbe<ProjectDesignSwapCompletedEventArgs>();

        stand.Host.SwapCompleted += swaps.Record;

        IDisposable deferral = stand.Host.Gate.Defer("a drag on the canvas");

        await stand.Host.BuildAsync("a test", cancellationToken: TestContext.Current.CancellationToken);

        // Whatever the host started on its own is over; a swap the deferral did not hold off would be in it.
        await stand.Host.SwapInFlight.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectDesignState.SwapPending, stand.Host.State);
        Assert.Equal(["a drag on the canvas"], stand.Host.Gate.Reasons);
        Assert.Empty(swaps.Seen);

        deferral.Dispose();

        await swaps.WhenCountAsync(1);

        Assert.True(swaps.Seen[0].Report.Reclaimed);
        Assert.Equal(ProjectDesignState.Live, stand.Host.State);
    }

    [AvaloniaFact]
    public async Task SwapAsync_Participants_LetGoBeforeTheDocumentsAndTakeUpAfter()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        var participant = new ParkingParticipant { Observe = () => document.State.ToString() };

        using IDisposable registration = stand.Host.Register(participant);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.True(report.Reclaimed);
        Assert.Equal(["release:Live", "restore:Live"], participant.Calls);
    }

    [AvaloniaFact]
    public async Task SwapAsync_TheDesignersOwnState_GoesLastBeforeTheReclaim()
    {
        var participant = new ParkingParticipant();

        await using DesignStand stand = await DesignStand.StartAsync(
            null,
            TestContext.Current.CancellationToken,
            options => options with
            {
                ReleaseHostState = _ =>
                {
                    participant.Calls.Add("host");

                    return ValueTask.CompletedTask;
                },
            });

        using IDisposable registration = stand.Host.Register(participant);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.True(report.Reclaimed);
        Assert.Equal(["release", "host", "restore"], participant.Calls);
    }

    [AvaloniaFact]
    public async Task OpenDocumentAsync_DuringASwap_WaitsForTheSuccessor()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        var participant = new ParkingParticipant { Parks = true };

        using IDisposable registration = stand.Host.Register(participant);

        Task<ProjectDesignSwapReport> swapping = stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken).AsTask();

        await participant.Arrived;

        Task<XamlLiveDocument> opening = stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        Assert.False(opening.IsCompleted, "A document opened while the generation was being replaced did not wait for the successor.");
        Assert.Equal(ProjectDesignState.Swapping, stand.Host.State);

        participant.LetGo();

        ProjectDesignSwapReport report = await swapping;
        XamlLiveDocument document = await opening;

        Assert.Equal(report.Successor, DesignStand.GenerationOf(document));
    }

    [AvaloniaFact]
    public async Task SwapAsync_AWindowRootedDocument_ClosesTheWindowItReplaced()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureWindow.axaml", TestContext.Current.CancellationToken);
        TaskCompletionSource closed = WatchClosing(document);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.True(closed.Task.IsCompleted, "The window the replaced session built was not closed.");
        Assert.True(report.Reclaimed);
        Assert.IsType<Window>(document.Session?.RootObject, exactMatch: false);
        Assert.Equal(report.Successor, DesignStand.GenerationOf(document));
    }

    [AvaloniaFact]
    public async Task SwapAsync_AGenerationStillHeld_RequiresARestartAndMakesNoSuccessor()
    {
        await using DesignStand stand = await DesignStand.StartAsync(
            (fixtures, _) => fixtures.Write("Holder.axaml", Holder),
            TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("Holder.axaml", TestContext.Current.CancellationToken);
        WeakReference<object> subscribed = ContentOf(document);

        var restarts = new EventProbe<ProjectDesignRestartEventArgs>();

        stand.Host.RestartRequired += restarts.Record;

        try
        {
            ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

            Assert.False(report.Reclaimed);
            Assert.Null(report.Successor);
            Assert.Null(stand.Host.GenerationName);
            Assert.Equal(ProjectDesignState.RestartRequired, stand.Host.State);
            Assert.Equal(ProjectDesignRestartReason.GenerationStillHeld, Assert.Single(restarts.Seen).Reason);
            Assert.Equal(XamlLiveDocumentState.Detached, document.State);

            ProjectDesignSwapReport again = await stand.Host.SwapAsync("again", TestContext.Current.CancellationToken);

            Assert.False(again.Reclaimed);
            Assert.Single(restarts.Seen);
        }
        finally
        {
            Unsubscribe(subscribed);
        }
    }

    [AvaloniaFact]
    public async Task SwapAsync_ADocumentWithUnsavedEdits_KeepsItsTextAndHistory()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "edited"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.True(report.Reclaimed);
        Assert.True(document.IsDirty);
        Assert.Equal("Set Tag", document.UndoDescription);
        Assert.Contains("Tag=\"edited\"", document.Document.SourceText.ToString(), StringComparison.Ordinal);
        Assert.Equal(XamlLiveDocumentState.Live, document.State);
        Assert.Equal(report.Successor, DesignStand.GenerationOf(document));
    }

    [AvaloniaFact]
    public async Task SwapAsync_ADocumentNobodyLooksAt_StaysDetachedUntilShown()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument shown = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        XamlLiveDocument hidden = await stand.OpenAsync("FixtureWindow.axaml", TestContext.Current.CancellationToken);

        stand.Host.SetVisibleDocuments([shown]);

        ProjectDesignSwapReport report = await stand.Host.SwapAsync("a test", TestContext.Current.CancellationToken);

        Assert.Equal(XamlLiveDocumentState.Live, shown.State);
        Assert.Equal(XamlLiveDocumentState.Detached, hidden.State);

        await stand.Host.EnsureLiveAsync(hidden, TestContext.Current.CancellationToken);

        Assert.Equal(XamlLiveDocumentState.Live, hidden.State);
        Assert.Equal(report.Successor, DesignStand.GenerationOf(hidden));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TaskCompletionSource WatchClosing(XamlLiveDocument document)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = (Window)document.Session!.RootObject;

        window.Closed += (_, _) => closed.TrySetResult();

        return closed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> ContentOf(XamlLiveDocument document) =>
        new(((ContentControl)document.Session!.RootObject).Content!);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unsubscribe(WeakReference<object> subscribed)
    {
        if (subscribed.TryGetTarget(out object? control))
        {
            control.GetType().GetMethod("Unsubscribe")!.Invoke(control, null);
        }
    }
}
