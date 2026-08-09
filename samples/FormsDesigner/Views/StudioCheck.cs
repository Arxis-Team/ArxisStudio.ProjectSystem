using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FormsDesigner.ViewModels;

namespace FormsDesigner.Views;

/// <summary>
/// Drives the studio through the whole of what it claims to do, and says whether it did it.
/// </summary>
/// <remarks>
/// <para>
/// A designer is a claim — "you can build an application in this" — and the only honest way to
/// check a claim like that is to make one. <c>--verify &lt;folder&gt;</c> creates a project from a
/// template, opens its window in the designer, lays controls onto it through the same code path the
/// toolbox uses, edits a property through the inspector's own rows, saves, builds and runs it, then
/// stops what it started and reports.
/// </para>
/// <para>
/// Every step goes through the view model rather than around it. A check that wrote the markup
/// itself would prove that this file can write XAML; going through <c>Drop</c>, the inspector's
/// rows and the commands proves the studio can.
/// </para>
/// <para>
/// Waits are on the clock and bounded, because these steps are MSBuild and a process start. There
/// is nothing to coordinate on from outside — that is the difference between this and the tests
/// under <c>tests/</c>, which are forbidden from sleeping precisely because they have something.
/// </para>
/// </remarks>
internal static class StudioCheck
{
    public static void RunWhenShown(MainWindow window, DesignerViewModel designer, string folder) =>
        window.Opened += (_, _) => _ = RunAsync(window, designer, folder);

    private static async Task RunAsync(Window window, DesignerViewModel designer, string folder)
    {
        var failures = 0;

        try
        {
            failures = await CheckAsync(designer, folder);
        }
        catch (Exception error)
        {
            Say($"the check itself failed: {error.GetType().Name}: {error.Message}");

            failures++;
        }

        Say(failures == 0
            ? "VERDICT ok — a project was created, laid out, built and run"
            : $"VERDICT {failures} step(s) failed");

        window.Close();
    }

    private static async Task<int> CheckAsync(DesignerViewModel designer, string folder)
    {
        var failures = 0;

        string name = "StudioCheckApp";

        // 1. A project, made the way the studio makes one: through the welcome screen, which is what
        //    answers the dialog, writes the files, remembers the project and names it to whoever
        //    opens it. Only the dialog is stood in for — the rest is the path a person takes.
        var welcome = new WelcomeViewModel
        {
            AskForNewProject = _ => Task.FromResult<NewProjectRequest?>(
                new NewProjectRequest(name, folder, ProjectScaffold.Templates[0])),
        };

        string? project = null;

        welcome.ProjectChosen += (_, chosen) => project = chosen;

        welcome.NewProjectCommand.Execute(null);

        if (!await Until(() => project is not null, 120))
        {
            return Fail(ref failures, "the welcome screen never produced a project: " + welcome.Problem);
        }

        Say($"created {project}");

        if (!welcome.Recent.Any(entry => entry.Path == project))
        {
            Fail(ref failures, "the new project is not in the recent list");
        }

        // 2. Opened, which is an MSBuild evaluation of something that has never been restored.
        designer.OpenAtStartup(project!, "MainWindow.axaml");

        if (!await Until(() => designer.IsLoaded && !designer.IsBusy, 240))
        {
            return Fail(ref failures, "the project never finished loading");
        }

        if (!await Until(() => designer.ActiveForm is { Session: not null }, 240))
        {
            return Fail(ref failures, "MainWindow.axaml never opened on the canvas");
        }

        FormViewModel form = designer.ActiveForm!;

        Say($"opened {form.Name}, root {form.Root?.GetType().Name ?? "none"}");

        // 3. Laid out: three controls from the toolbox, dropped into the form's panel.
        //
        // Into the panel rather than onto the root: a Window holds one child, which is what the
        // pointer path works out from what is under the cursor. The live control is found again
        // after every drop, because an edit rebuilds the objects the document produced.
        foreach ((string control, double y) in new[] { ("TextBlock", 40d), ("TextBox", 90d), ("Button", 140d) })
        {
            if (designer.Toolbox.FirstOrDefault(entry => entry.Name == control) is not { } tool)
            {
                Fail(ref failures, $"the toolbox has no {control}");

                continue;
            }

            int before = Count(designer, control);

            // The container is waited for rather than assumed. An edit republishes the live tree,
            // and for a moment between the document changing and the objects catching up there is
            // no panel to point at — a drop aimed at that moment lands on the root, which holds one
            // child and rejects it. A pointer never moves that fast; this does.
            if (!await Until(() => Find(designer, "StackPanel") is not null, 30))
            {
                Fail(ref failures, "the form's panel never came back after the last edit");

                continue;
            }

            designer.Drop(form, tool, over: Find(designer, "StackPanel"), at: new Point(60, y));

            if (!await Until(() => Count(designer, control) > before, 30))
            {
                Fail(ref failures, $"{control} never reached the document");
            }
        }

        Say($"document now: {Summary(designer)}");

        // 4. Edited: the button's own text, written through the inspector's row rather than around it.
        if (Find(designer, "Button") is { } button)
        {
            designer.SelectFromCanvas(form, button);

            if (designer.Properties.FirstOrDefault(row => row.Name == "Content") is { } content)
            {
                content.Value = "Поехали";

                if (!await Until(() => Text(form).Contains("Поехали", StringComparison.Ordinal), 30))
                {
                    Say("  the button's row now reads: " + content.Value);

                    Fail(ref failures, "the inspector's edit never reached the document");
                }
            }
            else
            {
                Fail(ref failures, "the inspector offered no Content row for a Button");
            }
        }
        else
        {
            Fail(ref failures, "no Button to edit");
        }

        // 4a. Duplicated and pasted, which is how a form with one of something gets three.
        if (Find(designer, "Button") is { } original)
        {
            designer.SelectFromCanvas(form, original);

            int buttons = Count(designer, "Button");

            designer.DuplicateCommand.Execute(null);

            if (!await Until(() => Count(designer, "Button") == buttons + 1, 30))
            {
                Fail(ref failures, "duplicate did not add a second Button");
            }

            Say($"  after duplicate the selection is {designer.Selected?.Name.LocalName ?? "none"}");

            designer.CopyCommand.Execute(null);

            await Task.Delay(300);

            designer.PasteCommand.Execute(null);

            if (!await Until(() => Count(designer, "Button") == buttons + 2, 30))
            {
                Fail(ref failures, "paste did not add a third Button");
            }

            // And back, so that what is built is the form that was laid out.
            designer.UndoCommand.Execute(null);
            await Until(() => Count(designer, "Button") == buttons + 1, 30);

            designer.UndoCommand.Execute(null);

            if (!await Until(() => Count(designer, "Button") == buttons, 30))
            {
                Fail(ref failures, "undo did not take the copies back");
            }

            Say($"duplicated and pasted, then undone: {Summary(designer)}");
        }

        // 4b. Undone and redone, which is the same path an edit takes and had better be.
        designer.UndoCommand.Execute(null);

        if (!await Until(() => !Text(form).Contains("Поехали", StringComparison.Ordinal), 30))
        {
            Fail(ref failures, "undo did not take the edit back");
        }

        designer.RedoCommand.Execute(null);

        if (!await Until(() => Text(form).Contains("Поехали", StringComparison.Ordinal), 30))
        {
            Fail(ref failures, "redo did not put the edit back");
        }

        // And far enough back that the form is the file again, which is what a clean form means.
        int steps = 0;

        while (designer.UndoCommand.CanExecute(null) && steps++ < 20)
        {
            designer.UndoCommand.Execute(null);

            await Until(() => !designer.IsBusy, 10);
            await Task.Delay(150);
        }

        if (form.IsDirty)
        {
            Fail(ref failures, "a form undone to where it started still says it has unsaved edits");
        }

        Say($"undone {steps} step(s) back to the file; document: {Summary(designer)}");

        for (int forward = 0; forward < steps && designer.RedoCommand.CanExecute(null); forward++)
        {
            designer.RedoCommand.Execute(null);

            await Until(() => !designer.IsBusy, 10);
            await Task.Delay(150);
        }

        if (!Text(form).Contains("Поехали", StringComparison.Ordinal))
        {
            Fail(ref failures, "redoing every step did not get back to the laid-out form");
        }

        // 4c. Named, because a control nothing can find by name is a control code-behind cannot use.
        if (Find(designer, "Button") is { } toName)
        {
            designer.SelectFromCanvas(form, toName);

            if (designer.Properties.FirstOrDefault(row => row.Name == "x:Name") is not { } named)
            {
                Fail(ref failures, "the inspector offers no name for a Button");
            }
            else
            {
                named.Value = "GoButton";

                if (!await Until(() => Text(form).Contains("x:Name=\"GoButton\"", StringComparison.Ordinal), 30))
                {
                    Fail(ref failures, "naming the button never reached the document");
                }
                else
                {
                    Say($"named the button, and the inspector calls the row \"{named.Heading}\"");
                }
            }
        }

        // 5. Saved, so that the build has something to compile.
        designer.SaveCommand.Execute(null);

        if (!await Until(() => form.IsDirty == false, 60))
        {
            Fail(ref failures, "the form never saved");
        }

        // 5a. Changed from outside, the way the IDE beside this one changes it.
        if (form.File.Value is { Length: > 0 } onDisk)
        {
            string outside = (await System.IO.File.ReadAllTextAsync(onDisk))
                .Replace("Поехали", "Извне", StringComparison.Ordinal);

            await System.IO.File.WriteAllTextAsync(onDisk, outside);

            if (!await Until(() => Text(form).Contains("Извне", StringComparison.Ordinal), 30))
            {
                Fail(ref failures, "an edit made outside never reached the open form");
            }
            else if (form.IsDirty)
            {
                Fail(ref failures, "a form reloaded from disk still calls itself edited");
            }
            else
            {
                Say("an edit made outside the designer arrived in the open form");
            }
        }

        // 6. Restored and built, which is the claim that what was laid out is a real application.
        designer.RestoreCommand.Execute(null);

        if (!await Until(() => !designer.IsBusy, 300, settle: true))
        {
            Fail(ref failures, "restore never finished");
        }

        designer.BuildCommand.Execute(null);

        if (!await Until(() => !designer.IsBusy, 300, settle: true))
        {
            Fail(ref failures, "build never finished");
        }

        if (designer.Diagnostics.Any(row => row.Severity == "ERROR"))
        {
            foreach (DiagnosticRow row in designer.Diagnostics.Where(row => row.Severity == "ERROR").Take(5))
            {
                Say($"  build error: {row.Message}");
            }

            Fail(ref failures, "the project did not build");
        }

        // 7. Run: the application it just laid out, started for real and then stopped.
        designer.RunCommand.Execute(null);

        if (!await Until(() => designer.IsRunning, 300))
        {
            Fail(ref failures, "the application never started");
        }
        else
        {
            await Task.Delay(2500);

            if (!designer.IsRunning)
            {
                Fail(ref failures, "the application exited on its own");
            }
            else
            {
                Say("the application is running");
            }

            designer.StopCommand.Execute(null);

            await Until(() => !designer.IsRunning, 30);
        }

        // 8. A form added and taken away again, which is the other half of a project panel.
        designer.AskForNewForm = _ => Task.FromResult<NewFormRequest?>(
            new NewFormRequest("SecondForm", NewFormKind.Window));

        designer.NewFormCommand.Execute(null);

        if (!await Until(() => designer.ProjectForms.Any(entry => entry.Name == "SecondForm.axaml"), 180))
        {
            Fail(ref failures, "the new form never appeared in the project");
        }
        else
        {
            string added = designer.ProjectForms.First(entry => entry.Name == "SecondForm.axaml").Path.Value;

            // What was made: a window, with a class the other editor can write code in.
            string madeMarkup = await System.IO.File.ReadAllTextAsync(added);

            if (!madeMarkup.Contains("<Window", StringComparison.Ordinal)
                || !madeMarkup.Contains("x:Class=", StringComparison.Ordinal))
            {
                Fail(ref failures, "the new window is not a window with a class");
            }
            else if (!System.IO.File.Exists(added + ".cs"))
            {
                Fail(ref failures, "the new window has no code-behind");
            }
            else
            {
                Say("the new form is a window with a class and a code-behind");
            }

            // And it opened. A form whose class the project has not compiled yet is the one every
            // designer gets wrong: the assembly is there, so nothing looks stale, and the load
            // answers that x:Class names a type no assembly has.
            if (!await Until(
                () => designer.Forms.FirstOrDefault(open => open.Name == "SecondForm.axaml")
                    is { Problem: null, Root: not null },
                240))
            {
                Fail(
                    ref failures,
                    "the new window did not load: "
                        + (designer.Forms.FirstOrDefault(open => open.Name == "SecondForm.axaml")?.Problem
                            ?? "it never opened"));
            }
            else
            {
                Say("the new window loaded, class and all");
            }

            designer.AskToConfirm = (_, _) => Task.FromResult(true);

            if (System.Linq.Enumerable.FirstOrDefault(
                designer.ProjectFiles, tile => tile.Name == "SecondForm.axaml") is not { } offered)
            {
                Fail(ref failures, "the new form is not a tile in the project pane");
            }
            else
            {
                designer.DeleteFile(offered);

                if (!await Until(() => !System.IO.File.Exists(added), 60))
                {
                    Fail(ref failures, "the form was not deleted from disk");
                }
                else if (!await Until(
                    () => designer.ProjectForms.All(entry => entry.Name != "SecondForm.axaml"), 180))
                {
                    Fail(ref failures, "the deleted form is still listed in the project");
                }
                else
                {
                    Say("a second form was created and deleted again");
                }
            }
        }

        // 8a. The tree's filter, save-all, and a close that asks about unsaved work.
        int allRows = designer.Hierarchy.Count;

        designer.IsHierarchySearchOpen = true;
        designer.HierarchyFilter = "Button";

        if (designer.Hierarchy.Count >= allRows || designer.Hierarchy.Count == 0)
        {
            Fail(ref failures, $"filtering the tree for Button left {designer.Hierarchy.Count} of {allRows} rows");
        }

        designer.IsHierarchySearchOpen = false;

        if (designer.Hierarchy.Count != allRows)
        {
            Fail(ref failures, "closing the tree's filter did not bring the rows back");
        }
        else
        {
            Say($"the tree filters to a few rows out of {allRows} and back");
        }

        // An edit, so that there is something to save and something to ask about.
        if (Find(designer, "Button") is { } dirtied)
        {
            designer.SelectFromCanvas(form, dirtied);

            if (designer.Properties.FirstOrDefault(row => row.Name == "Content") is { } content)
            {
                content.Value = "Ещё раз";

                await Until(() => form.IsDirty, 30);
            }
        }

        if (!form.IsDirty)
        {
            Fail(ref failures, "nothing to save after an edit");
        }
        else
        {
            designer.SaveAllCommand.Execute(null);

            if (!await Until(() => designer.Forms.All(open => !open.IsDirty), 60))
            {
                Fail(ref failures, "save all left a form unsaved");
            }
            else
            {
                Say("save all wrote every form that had edits");
            }
        }

        // Cancel keeps the tab; discard takes it away. Both are asked for.
        if (Find(designer, "Button") is { } again)
        {
            designer.SelectFromCanvas(form, again);

            if (designer.Properties.FirstOrDefault(row => row.Name == "Content") is { } content)
            {
                content.Value = "И ещё";

                await Until(() => form.IsDirty, 30);
            }
        }

        designer.AskToSave = _ => Task.FromResult(DesignerViewModel.SaveAnswer.Cancel);
        designer.CloseForm(form, ask: true);

        await Task.Delay(400);

        if (!designer.Forms.Contains(form))
        {
            Fail(ref failures, "cancelling the close still closed the form");
        }
        else
        {
            Say("a close that was cancelled kept the form");
        }

        designer.AskToSave = _ => Task.FromResult(DesignerViewModel.SaveAnswer.Discard);
        designer.CloseForm(form, ask: true);

        if (!await Until(() => !designer.Forms.Contains(form), 30))
        {
            Fail(ref failures, "discarding the edits did not close the form");
        }
        else
        {
            Say("a close that discarded the edits closed the form");
        }

        // 9. And the project file, whose change means the evaluation is stale.
        string wasSaying = designer.Status;

        System.IO.File.SetLastWriteTimeUtc(project!, DateTime.UtcNow);

        if (!await Until(() => designer.Status != wasSaying, 180))
        {
            Fail(ref failures, "a change to the project file did not re-read it");
        }
        else
        {
            Say("a change to the project file was picked up");
        }

        // 10. A form added by the other editor: a file appearing is a change to what the project is.
        string outsideForm = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(project!)!, "MadeOutside.axaml");

        await System.IO.File.WriteAllTextAsync(
            outsideForm,
            """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         Width="300" Height="200">
              <TextBlock Text="made outside" />
            </UserControl>
            """);

        if (!await Until(() => designer.ProjectForms.Any(entry => entry.Name == "MadeOutside.axaml"), 180))
        {
            Fail(ref failures, "a form added outside never appeared in the project");
        }
        else
        {
            Say("a form added outside appeared in the project");

            // 11. And taken away again while it is open here, which has to take the tab with it.
            if (System.Linq.Enumerable.FirstOrDefault(
                designer.ProjectFiles, tile => tile.Name == "MadeOutside.axaml") is { } tile)
            {
                designer.OpenFile(tile);

                if (!await Until(() => designer.Forms.Any(open => open.Name == "MadeOutside.axaml"), 120))
                {
                    Fail(ref failures, "the form added outside would not open");
                }
                else
                {
                    System.IO.File.Delete(outsideForm);

                    if (!await Until(() => designer.Forms.All(open => open.Name != "MadeOutside.axaml"), 120))
                    {
                        Fail(ref failures, "a form deleted outside is still open in the designer");
                    }
                    else
                    {
                        Say("a form deleted outside closed its tab");
                    }
                }
            }
            else
            {
                Fail(ref failures, "the form added outside is not a tile in the project pane");
            }
        }

        return failures;
    }

    /// <summary>The document as it stands, which is the text a save would write.</summary>
    private static string Text(FormViewModel form) =>
        form.Document?.SourceText.ToString() ?? string.Empty;

    /// <summary>How many elements of this name the document has, which is what a drop changes.</summary>
    private static int Count(DesignerViewModel designer, string element) =>
        designer.Hierarchy.Count(row => row.TypeLabel.StartsWith(element, StringComparison.Ordinal));

    /// <summary>The live control an element of this name produced, for the inspector to be pointed at.</summary>
    private static Control? Find(DesignerViewModel designer, string element)
    {
        if (designer.ActiveForm?.Objects is not { } map)
        {
            return null;
        }

        foreach (object produced in map.Objects)
        {
            if (produced is Control control
                && control.GetType().Name == element
                && map.GetElement(control) is not null)
            {
                return control;
            }
        }

        return null;
    }

    private static string Summary(DesignerViewModel designer) =>
        string.Join(", ", designer.Hierarchy.Select(row => row.Name));

    private static int Fail(ref int failures, string what)
    {
        Say("! " + what);

        return ++failures;
    }

    private static void Say(string line) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CHECK {line}"));

    /// <summary>
    /// Waits for something to become true, yielding to the dispatcher between looks.
    /// </summary>
    /// <param name="settle">
    /// Whether to require the condition twice over two ticks. A command that runs detached has not
    /// necessarily started by the time this first looks, and "not busy" is true before it as well as
    /// after it.
    /// </param>
    private static async Task<bool> Until(Func<bool> condition, int seconds, bool settle = false)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);

        if (settle)
        {
            await Task.Delay(400);
        }

        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
