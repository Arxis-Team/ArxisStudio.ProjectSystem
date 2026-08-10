using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ArxisStudio;
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

    /// <summary>Runs the endurance cycle instead of the straight-line check.</summary>
    public static void StressWhenShown(MainWindow window, DesignerViewModel designer, string folder) =>
        window.Opened += (_, _) => _ = StressRunAsync(window, designer, folder);

    private static async Task StressRunAsync(Window window, DesignerViewModel designer, string folder)
    {
        var failures = 0;

        try
        {
            failures = await StressAsync(window, designer, folder);
        }
        catch (Exception error)
        {
            Say($"the stress run itself failed: {error.GetType().Name}: {error.Message}");

            failures++;
        }

        Say(failures == 0
            ? "VERDICT ok — the designer survived every cycle"
            : $"VERDICT {failures} step(s) failed");

        window.Close();
    }

    /// <summary>
    /// The endurance cycle: edit, be edited from outside, delete bound blocks, repeat.
    /// </summary>
    /// <remarks>
    /// The designer's real life compressed: a window-rooted form with data bindings, edited here and
    /// in the other editor by turns, with whole bound subtrees deleted between rounds. What it
    /// asserts at every step is the invariant the designer lives by — the document, the live tree
    /// and the panels never disagree, and nothing on the way prints an error.
    /// </remarks>
    private static async Task<int> StressAsync(Window window, DesignerViewModel designer, string folder)
    {
        var failures = 0;

        // The blank template's window: a Window root, a StackPanel, and two TextBlocks bound to
        // Title and Greeting — data bindings from the first minute, which is the point.
        string project = await ProjectScaffold.CreateAsync(
            ProjectScaffold.Templates[0], folder, "StressApp", CancellationToken.None);

        // A control of the project's own, written before the studio opens it: the types a run holds
        // are the ones the project had when it was opened, so a control placed on a form has to be
        // part of that build. Creating one mid-run is the case the studio answers with a restart,
        // and this step checks that answer separately, below.
        string controlFile = await WriteControlAsync(project);

        designer.OpenAtStartup(project, "MainWindow.axaml");

        if (!await Until(() => designer.ActiveForm is { Session: not null, Problem: null }, 300))
        {
            return Fail(ref failures, "the window never opened: "
                + (designer.ActiveForm?.Problem ?? "no form"));
        }

        FormViewModel form = designer.ActiveForm!;
        string file = form.File.Value;
        int errorsSeen = CountErrors(designer);

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            Say($"— cycle {cycle} —");

            // 1. Several designer edits in a row: three controls dropped, then the window's own
            //    Title through the inspector — an edit on the root itself.
            foreach (string control in new[] { "Button", "TextBox", "CheckBox" })
            {
                if (designer.Toolbox.FirstOrDefault(entry => entry.Name == control) is not { } tool)
                {
                    Fail(ref failures, $"no {control} in the toolbox");

                    continue;
                }

                int before = Count(designer, control);

                if (!await Until(() => Find(designer, "StackPanel") is not null, 30))
                {
                    Fail(ref failures, $"cycle {cycle}: no panel to drop {control} into");

                    continue;
                }

                designer.Drop(form, tool, over: Find(designer, "StackPanel"), at: new Point(40, 40));

                if (!await Until(() => Count(designer, control) > before, 30))
                {
                    Fail(ref failures, $"cycle {cycle}: {control} never reached the document");
                }
            }

            if (form.Root is { } root)
            {
                designer.SelectFromCanvas(form, root);

                if (designer.Properties.FirstOrDefault(row => row.Name == "Title") is { } title)
                {
                    title.Value = $"Cycle {cycle}";

                    if (!await Until(
                        () => Text(form).Contains($"Title=\"Cycle {cycle}\"", StringComparison.Ordinal),
                        30))
                    {
                        Fail(ref failures, $"cycle {cycle}: the Title edit never reached the document");
                    }
                }
                else
                {
                    Fail(ref failures, $"cycle {cycle}: the inspector offers no Title for the window");
                }
            }

            designer.SaveCommand.Execute(null);

            await Until(() => !form.IsDirty, 60);

            // 2. The other editor's turn: a whole bound block appended inside the panel, written to
            //    disk the way a save is written.
            string block =
                $"    <StackPanel x:Name=\"OutsideBlock{cycle}\" Spacing=\"4\">\n"
                + "      <TextBlock Text=\"{Binding Greeting}\" />\n"
                + "      <TextBox Text=\"{Binding Title}\" />\n"
                + "      <Button Content=\"{Binding Title}\" />\n"
                + "    </StackPanel>\n";

            string text = await System.IO.File.ReadAllTextAsync(file);
            int closing = text.LastIndexOf("</StackPanel>", StringComparison.Ordinal);

            if (closing < 0)
            {
                Fail(ref failures, $"cycle {cycle}: the form has no panel to write into from outside");

                continue;
            }

            await System.IO.File.WriteAllTextAsync(file, text[..closing] + block + text[closing..]);

            if (!await Until(
                () => Text(form).Contains($"OutsideBlock{cycle}", StringComparison.Ordinal), 60))
            {
                Fail(ref failures, $"cycle {cycle}: the outside edit never arrived");

                continue;
            }

            if (form.IsDirty)
            {
                Fail(ref failures, $"cycle {cycle}: a reloaded form calls itself edited");
            }

            if (form.Problem is { } problem)
            {
                Fail(ref failures, $"cycle {cycle}: the reload failed: {problem}");
            }

            // The block is not just text in a file — it is live on the canvas, bindings and all.
            if (!await Until(() => LiveByName(designer, $"OutsideBlock{cycle}") is not null, 30))
            {
                Fail(ref failures, $"cycle {cycle}: the outside block never became live objects");
            }

            // 3. The bound block goes, as a block: one delete for the panel takes its three bound
            //    children with it.
            if (LiveByName(designer, $"OutsideBlock{cycle}") is { } live)
            {
                designer.SelectFromCanvas(form, live);

                designer.DeleteSelectedCommand.Execute(null);

                if (!await Until(
                    () => !Text(form).Contains($"OutsideBlock{cycle}", StringComparison.Ordinal), 30))
                {
                    Fail(ref failures, $"cycle {cycle}: the bound block would not delete");
                }
            }

            // 4. And back, and gone again — history over a document another editor wrote.
            designer.UndoCommand.Execute(null);

            if (!await Until(
                () => Text(form).Contains($"OutsideBlock{cycle}", StringComparison.Ordinal), 30))
            {
                Fail(ref failures, $"cycle {cycle}: undo did not bring the block back");
            }

            designer.RedoCommand.Execute(null);

            if (!await Until(
                () => !Text(form).Contains($"OutsideBlock{cycle}", StringComparison.Ordinal), 30))
            {
                Fail(ref failures, $"cycle {cycle}: redo did not take the block away again");
            }

            designer.SaveCommand.Execute(null);

            await Until(() => !form.IsDirty, 60);

            // 5. Nothing along the way said "!", and the form still loads clean.
            int errorsNow = CountErrors(designer);

            if (errorsNow > errorsSeen)
            {
                Fail(ref failures,
                    $"cycle {cycle}: {errorsNow - errorsSeen} error line(s) — see the console");

                errorsSeen = errorsNow;
            }

            Say($"cycle {cycle} done: {Summary(designer)}");
        }

        // What the cycles produced is still an application: build it and start it.
        designer.BuildCommand.Execute(null);

        if (!await Until(() => !designer.IsBusy, 300, settle: true))
        {
            Fail(ref failures, "the final build never finished");
        }

        if (designer.Diagnostics.Any(row => row.Severity == "Error"))
        {
            Fail(ref failures, "the final build failed");
        }

        designer.RunCommand.Execute(null);

        if (!await Until(() => designer.IsRunning, 300))
        {
            Fail(ref failures, "the application the cycles produced never started");
        }
        else
        {
            await Task.Delay(2000);

            if (!designer.IsRunning)
            {
                Fail(ref failures, "the application exited on its own");
            }

            designer.StopCommand.Execute(null);

            await Until(() => !designer.IsRunning, 30);
        }

        // 12. The palette and the inspector at the depth building an application needs: a Grid
        //     whose rows are written through the row editor, an items control that drops with
        //     visible items, and a control whose package the project does not have — refused in
        //     words, with the file untouched.
        if (designer.Toolbox.FirstOrDefault(entry => entry.Name == "Grid") is { } gridTool)
        {
            designer.Drop(form, gridTool, over: Find(designer, "StackPanel"), at: new Point(20, 20));

            if (!await Until(() => Find(designer, "Grid") is not null, 30))
            {
                Fail(ref failures, "the Grid never reached the document");
            }
            else
            {
                designer.SelectFromCanvas(form, Find(designer, "Grid")!);

                if (designer.Properties.FirstOrDefault(row => row.Name == "RowDefinitions") is { } rows)
                {
                    rows.Value = "Auto,*";

                    if (!await Until(
                        () => Text(form).Contains("RowDefinitions=\"Auto,*\"", StringComparison.Ordinal),
                        30))
                    {
                        Fail(ref failures, "the RowDefinitions edit never reached the document");
                    }
                    else
                    {
                        Say("a Grid's rows are written through the inspector");
                    }
                }
                else
                {
                    Fail(ref failures, "the inspector offers no RowDefinitions for a Grid");
                }
            }
        }

        if (designer.Toolbox.FirstOrDefault(entry => entry.Name == "ComboBox") is { } comboTool)
        {
            designer.Drop(form, comboTool, over: Find(designer, "StackPanel"), at: new Point(20, 20));

            if (!await Until(
                () => Text(form).Contains("<ComboBoxItem", StringComparison.Ordinal)
                    && Find(designer, "ComboBox") is not null,
                30))
            {
                Fail(ref failures, "the ComboBox did not drop with its starter items");
            }
            else
            {
                Say("an items control drops with visible items");
            }
        }

        if (designer.Toolbox.FirstOrDefault(entry => entry.Name == "DataGrid") is { } gridlessTool)
        {
            string beforeRefusal = Text(form);

            designer.Drop(form, gridlessTool, over: Find(designer, "StackPanel"), at: new Point(20, 20));

            if (!await Until(
                () => designer.Output.Any(line =>
                    line.Contains("Avalonia.Controls.DataGrid", StringComparison.Ordinal)),
                30))
            {
                Fail(ref failures, "a DataGrid without its package was not refused in words");
            }
            else if (Text(form) != beforeRefusal)
            {
                Fail(ref failures, "the refused DataGrid still reached the document");
            }
            else
            {
                Say("a control whose package is missing is refused, and the file is untouched");
            }
        }

        // 13. A resize of a form that states only design sizes — the way every template writes its
        //     windows. The card, the document and the live window must end up saying one number.
        string designSized = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(project!)!, "DesignSized.axaml");

        await System.IO.File.WriteAllTextAsync(
            designSized,
            """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    mc:Ignorable="d" d:DesignWidth="300" d:DesignHeight="200">
              <Canvas />
            </Window>
            """);

        if (!await Until(
            () => System.Linq.Enumerable.Any(designer.ProjectFiles, tile => tile.Name == "DesignSized.axaml"),
            180))
        {
            Fail(ref failures, "the design-sized form never appeared in the project");
        }
        else
        {
            designer.OpenFile(System.Linq.Enumerable.First(
                designer.ProjectFiles, tile => tile.Name == "DesignSized.axaml"));

            if (!await Until(
                () => designer.ActiveForm is { Problem: null, Root: not null } sized
                    && sized.Name == "DesignSized.axaml",
                120))
            {
                Fail(ref failures, "the design-sized form would not open");
            }
            else
            {
                FormViewModel sized = designer.ActiveForm!;
                DesignEditor? editor = window.FindControl<DesignEditor>("Surface");

                sized.Width = 520;
                sized.Height = 300;

                Control? card = null;

                if (!await Until(
                    () => (card = editor?.ContainerFromItem(sized) as Control) is { Bounds.Width: > 519 },
                    30))
                {
                    Fail(ref failures, "the card never took the new size");
                }
                else
                {
                    designer.WriteGeometry(sized, card!, moved: false, resized: true);

                    if (!await Until(
                        () => sized.Root is { Width: > 519 and < 521, Height: > 299 and < 301 }, 30))
                    {
                        Fail(
                            ref failures,
                            "after the resize was written, the live window says "
                                + $"{sized.Root?.Width}×{sized.Root?.Height} while the card says 520×300");
                    }
                    else
                    {
                        Say("a design-sized window resizes: card, document and live window agree");

                        // And an undone resize takes the card back with it. Undo rolls the document
                        // to the old size and the design values are applied to the live window
                        // again — the card used to stay where the drag left it, and the form
                        // overflowed its own frame the moment somebody edited anything else.
                        designer.UndoCommand.Execute(null);

                        if (!await Until(
                            () => sized.Root is { Width: > 299 and < 301 }
                                && sized.Width is > 299 and < 301
                                && sized.Height is > 199 and < 201,
                            30))
                        {
                            Fail(
                                ref failures,
                                "after undoing the resize the card says "
                                    + $"{sized.Width}×{sized.Height} while the window says "
                                    + $"{sized.Root?.Width}×{sized.Root?.Height}");
                        }
                        else
                        {
                            Say("an undone resize takes the card back with the document");

                            // The inspector is the other door to the same size. On a root that
                            // states only design sizes, the Width row must show the size the
                            // designer shows — not sit empty beside a canvas visibly 300 wide —
                            // and writing it must reach the design attribute, or the edit would
                            // be overruled by the next update the way the drag once was.
                            designer.SelectFromCanvas(sized, sized.Root!);

                            PropertyRow? width = designer.Properties.FirstOrDefault(
                                row => row.Name == "Width");

                            if (width is null || width.Value != "300")
                            {
                                Fail(ref failures, "the Width row of a design-sized root shows "
                                    + $"'{width?.Value}' while the designer shows 300");
                            }
                            else
                            {
                                width.Value = "480";

                                if (!await Until(
                                    () => sized.Root is { Width: > 479 and < 481 }
                                        && Text(sized).Contains(
                                            "d:DesignWidth=\"480\"", StringComparison.Ordinal),
                                    30))
                                {
                                    Fail(ref failures, "a Width typed into the inspector did not "
                                        + "reach the design size: the window says "
                                        + $"{sized.Root?.Width} and the document says "
                                        + (Text(sized).Contains("d:DesignWidth=\"480\"", StringComparison.Ordinal)
                                            ? "480" : "something else"));
                                }
                                else
                                {
                                    Say("the inspector edits the size the designer shows");
                                }
                            }
                        }
                    }
                }

                designer.CloseForm(sized);
            }
        }

        failures += await EmbeddedControlAsync(designer, form, controlFile);

        return failures;
    }

    /// <summary>
    /// 14. A project's own control, placed on another form — the designer's hardest ordinary case.
    /// </summary>
    /// <remarks>
    /// Everything about an embedded control crosses a boundary: its instance says it came from its
    /// own document, so the object map refuses it; its rendering comes from the compiled assembly,
    /// so a saved edit is invisible until a rebuild; and a rebuild makes a second copy of the
    /// assembly, which is where "unable to substitute MyControl with MyControl" lived. This step
    /// walks the whole story: place it, select it, inspect it, edit and save its source, see the
    /// placement update, survive a reopen after an outside code edit, and delete it.
    /// </remarks>
    private static async Task<int> EmbeddedControlAsync(
        DesignerViewModel designer, FormViewModel form, string controlFile)
    {
        var failures = 0;

        if (form.Document?.Root?.GetDirective("Class") is not { Length: > 0 } mainClass)
        {
            return Fail(ref failures, "the stress window declares no class to derive a namespace from");
        }

        string space = mainClass[..mainClass.LastIndexOf('.')];

        // Saved and closed before anything is written outside: an open dirty form rightly refuses
        // an outside overwrite, and the placement is written into the file while it is closed.
        designer.SaveCommand.Execute(null);

        if (!await Until(() => !form.IsDirty, 60))
        {
            return Fail(ref failures, "the window would not save before the embedded step");
        }

        designer.CloseForm(form);

        // Placed the way the other editor would place it: written into the file while the form is
        // closed. The open below is what builds the project, which is what makes the type exist.
        string text = await System.IO.File.ReadAllTextAsync(form.File.Value);
        int closing = text.LastIndexOf("</StackPanel>", StringComparison.Ordinal);
        int root = text.IndexOf("<Window", StringComparison.Ordinal);

        if (closing < 0 || root < 0)
        {
            return Fail(ref failures, "the stress window lost the shape this step writes into");
        }

        text = text.Insert(closing, "    <v:MyControl x:Name=\"Embedded\" />\n")
            .Insert(root + "<Window".Length, $" xmlns:v=\"using:{space}\"");

        await System.IO.File.WriteAllTextAsync(form.File.Value, text);

        if (!Open(designer, form.Name))
        {
            return Fail(ref failures, "the window left the project when it was closed");
        }

        if (!await Until(
            () => designer.ActiveForm is { Problem: null, Root: not null } reopened
                && reopened.Name == form.Name
                && LiveByName(designer, "Embedded") is not null,
            300))
        {
            return Fail(ref failures, "the window did not come back with the embedded control live: "
                + (designer.ActiveForm?.Problem ?? "no problem reported"));
        }

        FormViewModel window = designer.ActiveForm!;

        // Selecting the embedded control must select it, not the window around it.
        designer.SelectFromCanvas(window, LiveByName(designer, "Embedded"));

        if (designer.Selected?.Name.LocalName != "MyControl")
        {
            Fail(ref failures, "clicking the embedded control selected "
                + (designer.Selected?.Name.ToString() ?? "nothing"));
        }
        else if (!System.Linq.Enumerable.Any(designer.Properties, row => row.Name == "Width"))
        {
            Fail(ref failures, "the embedded control's inspector offers no Width");
        }
        else
        {
            Say("a placed project control is selectable and inspectable");
        }

        // Edit the control's own form and save: the placement must take the new shape without
        // anybody pressing Build.
        if (!await Until(() => Open(designer, "MyControl.axaml"), 120))
        {
            return Fail(ref failures, "MyControl.axaml never appeared in the project");
        }

        if (!await Until(
            () => designer.ActiveForm is { Problem: null, Root: not null } opened
                && opened.Name == "MyControl.axaml",
            300))
        {
            return Fail(ref failures, "the control's own form would not open: "
                + (designer.ActiveForm?.Problem ?? "no problem reported"));
        }

        FormViewModel control = designer.ActiveForm!;

        designer.SelectFromCanvas(control, Find(designer, "TextBlock"));

        if (System.Linq.Enumerable.FirstOrDefault(
            designer.Properties, row => row.Name == "Text") is not { } textRow)
        {
            Fail(ref failures, "the control's TextBlock offers no Text row");
        }
        else
        {
            textRow.Value = "Second";

            if (!await Until(
                () => Text(control).Contains("Second", StringComparison.Ordinal), 30))
            {
                Fail(ref failures, "the Text edit never reached the control's document");
            }
        }

        designer.SaveCommand.Execute(null);

        // The placed copy draws from the compiled assembly, so it cannot follow this save — and the
        // studio must say so rather than leave the canvas quietly behind the file.
        if (!await Until(() => designer.NeedsRestart, 60))
        {
            Fail(ref failures, "saving a placed control said nothing about the copies that are behind");
        }
        else
        {
            Say("saving a placed control says which forms are now behind it");
        }

        // The exact reported break: edit the code-behind outside, close the form, open it again.
        // A second generation used to be made here, and the two copies of one assembly met as
        // "Unable to substitute MyControl with MyControl".
        designer.CloseForm(designer.Forms.First(f => f.Name == "MyControl.axaml"));

        await System.IO.File.AppendAllTextAsync(controlFile + ".cs", "\n// touched outside\n");

        if (!Open(designer, "MyControl.axaml"))
        {
            return Fail(ref failures, "MyControl.axaml left the project before the reopen");
        }

        if (!await Until(
            () => designer.ActiveForm is { Problem: null, Root: not null } back
                && back.Name == "MyControl.axaml",
            300))
        {
            Fail(ref failures, "reopening after an outside code edit failed: "
                + (designer.ActiveForm?.Problem ?? "no problem reported"));
        }
        else
        {
            Say("a reopen after an outside code edit still loads");
        }

        // And the placement deletes like anything else.
        if (!Open(designer, form.Name))
        {
            return Fail(ref failures, "the window left the project before the delete");
        }

        if (!await Until(
            () => designer.ActiveForm is { } active && active.Name == form.Name
                && LiveByName(designer, "Embedded") is not null,
            60))
        {
            return Fail(ref failures, "the window would not come forward for the delete");
        }

        FormViewModel target = designer.ActiveForm!;

        designer.SelectFromCanvas(target, LiveByName(designer, "Embedded"));
        designer.DeleteSelectedCommand.Execute(null);

        if (!await Until(
            () => !Text(target).Contains("<v:MyControl", StringComparison.Ordinal), 30))
        {
            Fail(ref failures, "the embedded control would not delete");
        }
        else
        {
            Say("a placed control deletes like anything else");
        }

        return failures;
    }

    /// <summary>Writes a control of the project's own beside its window, before anything opens it.</summary>
    private static async Task<string> WriteControlAsync(string project)
    {
        string views = System.IO.Path.GetDirectoryName(
            System.IO.Directory.GetFiles(
                System.IO.Path.GetDirectoryName(project)!, "MainWindow.axaml", System.IO.SearchOption.AllDirectories)
                .First())!;

        string file = System.IO.Path.Combine(views, "MyControl.axaml");

        await System.IO.File.WriteAllTextAsync(
            file,
            """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="StressApp.Views.MyControl">
              <Border Background="#333B4A" Padding="16">
                <TextBlock Text="First" />
              </Border>
            </UserControl>
            """);

        await System.IO.File.WriteAllTextAsync(
            file + ".cs",
            """
            using Avalonia.Controls;
            using Avalonia.Markup.Xaml;

            namespace StressApp.Views;

            public partial class MyControl : UserControl
            {
                public MyControl() => AvaloniaXamlLoader.Load(this);
            }
            """);

        return file;
    }

    /// <summary>Opens a project form by file name, the way a double-click in the pane would.</summary>
    private static bool Open(DesignerViewModel designer, string name)
    {
        if (designer.ProjectForms.FirstOrDefault(form => form.Name == name) is not { } form)
        {
            return false;
        }

        designer.OpenFile(new FileTile(
            form.Name, form.Path, Glyphs.ForFile(form.Path.Extension), Glyphs.HueOfFile(form.Path.Extension)));

        return true;
    }

    /// <summary>The live control behind the element carrying this <c>x:Name</c>.</summary>
    private static Control? LiveByName(DesignerViewModel designer, string name)
    {
        if (designer.ActiveForm is not { Objects: not null, Document.Root: { } root } form)
        {
            return null;
        }

        foreach (ArxisStudio.Markup.Xaml.XamlElement element in Elements(root))
        {
            if (element.GetDirective("Name") == name)
            {
                return DesignerViewModel.ControlFor(form, element);
            }
        }

        return null;

        static System.Collections.Generic.IEnumerable<ArxisStudio.Markup.Xaml.XamlElement> Elements(
            ArxisStudio.Markup.Xaml.XamlElement element)
        {
            yield return element;

            foreach (ArxisStudio.Markup.Xaml.XamlElement child in element.ContentElements)
            {
                foreach (ArxisStudio.Markup.Xaml.XamlElement grand in Elements(child))
                {
                    yield return grand;
                }
            }
        }
    }

    /// <summary>How many lines of the console are complaints.</summary>
    private static int CountErrors(DesignerViewModel designer) =>
        designer.Output.Count(line => line.TrimStart().StartsWith('!'));

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

        // 4b2. Moved among its siblings, wrapped, and unwrapped — the structure edits, each of
        //      which must be exactly one step of the history.
        if (Find(designer, "Button") is { } ordered)
        {
            designer.SelectFromCanvas(form, ordered);

            int buttonAt = Text(form).IndexOf("<Button", StringComparison.Ordinal);
            int boxAt = Text(form).IndexOf("<TextBox", StringComparison.Ordinal);

            if (buttonAt < boxAt)
            {
                Fail(ref failures, "the drops did not land in the expected order");
            }

            designer.MoveUpCommand.Execute(null);

            if (!await Until(
                () => Text(form).IndexOf("<Button", StringComparison.Ordinal)
                    < Text(form).IndexOf("<TextBox", StringComparison.Ordinal),
                30))
            {
                Fail(ref failures, "move up did not reorder the siblings");
            }

            designer.MoveDownCommand.Execute(null);

            if (!await Until(
                () => Text(form).IndexOf("<Button", StringComparison.Ordinal)
                    > Text(form).IndexOf("<TextBox", StringComparison.Ordinal),
                30))
            {
                Fail(ref failures, "move down did not put the sibling back");
            }

            designer.Wrap("Border");

            if (!await Until(
                () => Text(form).Contains("<Border><Button", StringComparison.Ordinal), 30))
            {
                Fail(ref failures, "wrap did not put a Border around the Button");
            }
            else if (designer.Selected?.Name.LocalName != "Border")
            {
                Fail(ref failures, "the selection did not follow the wrap to the Border");
            }
            else
            {
                designer.Unwrap();

                if (!await Until(
                    () => !Text(form).Contains("<Border>", StringComparison.Ordinal), 30))
                {
                    Fail(ref failures, "unwrap did not lift the Button back out");
                }
                else
                {
                    designer.UndoCommand.Execute(null);

                    if (!await Until(
                        () => Text(form).Contains("<Border><Button", StringComparison.Ordinal), 30))
                    {
                        Fail(ref failures, "undoing the unwrap is not one step");
                    }

                    designer.RedoCommand.Execute(null);
                    await Until(() => !Text(form).Contains("<Border>", StringComparison.Ordinal), 30);
                }
            }

            Say("moved, wrapped, unwrapped — one history step each");
        }

        // 4b3. The inspector's rows filter down and come back.
        if (Find(designer, "Button") is { } filtered)
        {
            designer.SelectFromCanvas(form, filtered);

            int fullRows = designer.PropertyGroups.Sum(group => group.Rows.Count);

            designer.InspectorFilter = "Width";

            int narrowed = designer.PropertyGroups.Sum(group => group.Rows.Count);

            designer.InspectorFilter = string.Empty;

            if (narrowed == 0 || narrowed >= fullRows)
            {
                Fail(ref failures, $"filtering the inspector for Width left {narrowed} of {fullRows} rows");
            }
            else if (designer.PropertyGroups.Sum(group => group.Rows.Count) != fullRows)
            {
                Fail(ref failures, "clearing the inspector's filter did not bring the rows back");
            }
            else
            {
                Say($"the inspector filters to {narrowed} row(s) out of {fullRows} and back");
            }
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

            // And what happens when it is opened, which is the honest half of one generation per
            // run: the class was compiled after this run loaded its types, so the studio cannot
            // show it — and must say so and offer the reload rather than fail quietly.
            if (!await Until(
                () => designer.Forms.Any(open => open.Name == "SecondForm.axaml"), 240))
            {
                Fail(ref failures, "the new window never opened at all");
            }
            else if (!await Until(() => designer.NeedsRestart, 240))
            {
                Fail(ref failures, "a form whose class is newer than this run said nothing about it");
            }
            else if (!designer.RestartCommand.CanExecute(null))
            {
                Fail(ref failures, "the studio said a reload was needed and would not perform one");
            }
            else
            {
                Say("a form newer than this run's types offers the reload that would show it");
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
