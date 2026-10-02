using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// What the other editor does to the files of open documents, as the owner's watcher reports it:
/// a save taken as a step, a save over unsaved edits held as a conflict, a rename followed, a deletion
/// reported, and a control's save shown in the forms that place it — in place, their sessions kept.
/// </summary>
[Collection(FixtureGenerations.Name)]
public sealed class DesignHostChangeTests
{
    [AvaloniaFact]
    public async Task NotifyChanged_AFormSavedElsewhere_IsTakenAsAStepOfItsHistory()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        var changes = new EventProbe<ProjectDesignChangesEventArgs>();

        stand.Host.ChangesApplied += changes.Record;

        CanonicalPath file = SaveElsewhere(stand, "FixtureControl.axaml", "Compiled", "Saved elsewhere");

        stand.Host.NotifyChanged([new FileChange(file, FileChangeKind.Changed)]);

        await changes.WhenCountAsync(1);

        Assert.Contains("Saved elsewhere", document.Document.SourceText.ToString(), StringComparison.Ordinal);
        Assert.False(document.IsDirty);
        Assert.Equal("Changed outside the designer", document.UndoDescription);
        Assert.Equal(XamlLiveDocumentState.Live, document.State);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AFormSavedElsewhereOverUnsavedEdits_IsAConflictThatChangesNothing()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "mine"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        var conflicts = new EventProbe<ProjectDesignConflictEventArgs>();

        stand.Host.ExternalConflict += conflicts.Record;

        CanonicalPath file = SaveElsewhere(stand, "FixtureControl.axaml", "Compiled", "Theirs");

        stand.Host.NotifyChanged([new FileChange(file, FileChangeKind.Changed)]);

        await conflicts.WhenCountAsync(1);

        ProjectDesignConflictEventArgs conflict = conflicts.Seen[0];
        string text = document.Document.SourceText.ToString();

        Assert.Same(document, conflict.Document);
        Assert.Equal(file, conflict.File);
        Assert.Contains("Theirs", conflict.DiskText, StringComparison.Ordinal);
        Assert.Contains("Tag=\"mine\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Theirs", text, StringComparison.Ordinal);
        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AFormRenamedElsewhere_IsFollowedWithItsHistory()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "kept"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        var moves = new EventProbe<ProjectDesignDocumentEventArgs>();

        stand.Host.DocumentMoved += moves.Record;

        CanonicalPath before = stand.Fixtures.Document("FixtureControl.axaml");
        CanonicalPath after = stand.Fixtures.Document("Renamed.axaml");

        File.Move(before.Value, after.Value);

        stand.Host.NotifyChanged([FileChange.Renamed(before, after)]);

        await moves.WhenCountAsync(1);

        Assert.Equal(before, moves.Seen[0].Previous);
        Assert.Equal(after, moves.Seen[0].File);
        Assert.True(stand.Host.TryGetFile(document, out CanonicalPath followed));
        Assert.Equal(after, followed);
        Assert.EndsWith("/Renamed.axaml", document.Uri.ToString(), StringComparison.Ordinal);
        Assert.Equal("Set Tag", document.UndoDescription);
        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AFormDeletedElsewhere_IsReportedAndStaysOpen()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);
        var deletions = new EventProbe<ProjectDesignDocumentEventArgs>();

        stand.Host.DocumentDeleted += deletions.Record;

        CanonicalPath file = stand.Fixtures.Document("FixtureControl.axaml");

        File.Delete(file.Value);

        stand.Host.NotifyChanged([new FileChange(file, FileChangeKind.Deleted)]);

        await deletions.WhenCountAsync(1);

        Assert.Same(document, deletions.Seen[0].Document);
        Assert.Contains(document, stand.Host.Documents);

        await stand.Host.CloseDocumentAsync(document, TestContext.Current.CancellationToken);

        Assert.Empty(stand.Host.Documents);
    }

    [AvaloniaFact]
    public async Task NotifyChanged_AControlSavedElsewhere_IsShownInTheFormThatPlacesIt()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument window = await stand.OpenAsync("FixtureWindow.axaml", TestContext.Current.CancellationToken);

        Assert.Equal("Compiled", CaptionOf(window));

        XamlLoadSession session = window.Session!;
        var changes = new EventProbe<ProjectDesignChangesEventArgs>();

        stand.Host.ChangesApplied += changes.Record;

        CanonicalPath control = SaveElsewhere(stand, "FixtureControl.axaml", "Compiled", "Saved elsewhere");

        stand.Host.NotifyChanged([new FileChange(control, FileChangeKind.Changed)]);

        await changes.WhenCountAsync(1);

        // The placed control is built again, and the window around it is not: same session, same root.
        Assert.Equal("Saved elsewhere", CaptionOf(window));
        Assert.Same(session, window.Session);
    }

    [AvaloniaFact]
    public async Task EditAsync_AnOpenControlEdited_IsShownInTheFormThatPlacesIt()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument window = await stand.OpenAsync("FixtureWindow.axaml", TestContext.Current.CancellationToken);
        XamlLiveDocument control = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        XamlLoadSession session = window.Session!;
        TaskCompletionSource rebuilt = NextObjects(window);

        await control.EditAsync(
            editor =>
            {
                XamlElement caption = editor.Document.Root!.Elements.Single();

                editor.SetAttribute(caption, new XamlQualifiedName(null, "Text"), "Unsaved");
            },
            "Set Text",
            TestContext.Current.CancellationToken);

        await rebuilt.Task.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken);

        Assert.Equal("Unsaved", CaptionOf(window));
        Assert.Same(session, window.Session);
    }

    [AvaloniaFact]
    public async Task CloseDocumentAsync_AControlWithUnsavedEdits_PutsTheFormsThatPlaceItBackOnItsFile()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument window = await stand.OpenAsync("FixtureWindow.axaml", TestContext.Current.CancellationToken);
        XamlLiveDocument control = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        XamlLoadSession session = window.Session!;
        TaskCompletionSource edited = NextObjects(window);

        await control.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!.Elements.Single(), new XamlQualifiedName(null, "Text"), "Unsaved"),
            "Set Text",
            TestContext.Current.CancellationToken);

        await edited.Task.WaitAsync(DesignStand.Patience, TestContext.Current.CancellationToken);

        Assert.Equal("Unsaved", CaptionOf(window));

        await stand.Host.CloseDocumentAsync(control, TestContext.Current.CancellationToken);

        Assert.Equal("Compiled", CaptionOf(window));
        Assert.Same(session, window.Session);
    }

    [AvaloniaFact]
    public async Task ReloadAsync_ARestoredDocumentWhoseFileMovedOn_IsAConflict()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        CanonicalPath file = stand.Fixtures.Document("FixtureControl.axaml");
        string saved = await File.ReadAllTextAsync(file.Value, TestContext.Current.CancellationToken);

        SaveElsewhere(stand, "FixtureControl.axaml", "Compiled", "Moved on");

        XamlLiveDocument document = await stand.Host.OpenDocumentAsync(
            file,
            new ProjectDesignDocumentOptions { Text = saved.Replace("Compiled", "Restored", StringComparison.Ordinal), SavedText = saved },
            TestContext.Current.CancellationToken);

        var conflicts = new EventProbe<ProjectDesignConflictEventArgs>();

        stand.Host.ExternalConflict += conflicts.Record;

        XamlExternalTextResult result = await stand.Host.ReloadAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(XamlExternalTextOutcome.Conflict, result.Outcome);
        Assert.Single(conflicts.Seen);
        Assert.Contains("Restored", document.Document.SourceText.ToString(), StringComparison.Ordinal);
        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task SaveAsync_ADocument_WritesItsTextAndIsClean()
    {
        await using DesignStand stand = await DesignStand.StartAsync(null, TestContext.Current.CancellationToken);

        XamlLiveDocument document = await stand.OpenAsync("FixtureControl.axaml", TestContext.Current.CancellationToken);

        await document.EditAsync(
            editor => editor.SetAttribute(editor.Document.Root!, new XamlQualifiedName(null, "Tag"), "saved"),
            "Set Tag",
            TestContext.Current.CancellationToken);

        await stand.Host.SaveAsync(document, TestContext.Current.CancellationToken);

        string written = await File.ReadAllTextAsync(stand.Fixtures.Document("FixtureControl.axaml").Value, TestContext.Current.CancellationToken);

        Assert.Equal(document.Document.SourceText.ToString(), written);
        Assert.False(document.IsDirty);
    }

    /// <summary>Rewrites one of the project's documents, as the other editor saves it.</summary>
    private static CanonicalPath SaveElsewhere(DesignStand stand, string name, string from, string to)
    {
        CanonicalPath file = stand.Fixtures.Document(name);

        File.WriteAllText(file.Value, File.ReadAllText(file.Value).Replace(from, to, StringComparison.Ordinal));

        return file;
    }

    /// <summary>What the placed control's caption says in a window document's root.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CaptionOf(XamlLiveDocument window)
    {
        var root = (Window)window.Session!.RootObject;

        return root.GetLogicalDescendants()
            .OfType<UserControl>()
            .Select(static control => control.Content)
            .OfType<TextBlock>()
            .Select(static caption => caption.Text)
            .FirstOrDefault();
    }

    /// <summary>Completes when objects of a document are built again in place.</summary>
    private static TaskCompletionSource NextObjects(XamlLiveDocument document)
    {
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, XamlLiveDocumentChangedEventArgs e)
        {
            if (e.Changes.HasFlag(XamlLiveDocumentChanges.Objects))
            {
                document.Changed -= OnChanged;
                rebuilt.TrySetResult();
            }
        }

        document.Changed += OnChanged;

        return rebuilt;
    }
}
