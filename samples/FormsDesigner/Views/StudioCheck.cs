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

        // 5. Saved, so that the build has something to compile.
        designer.SaveCommand.Execute(null);

        if (!await Until(() => form.IsDirty == false, 60))
        {
            Fail(ref failures, "the form never saved");
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
