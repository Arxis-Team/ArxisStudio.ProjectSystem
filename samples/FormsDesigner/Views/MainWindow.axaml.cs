using System.Threading.Tasks;
using ArxisStudio;
using ArxisStudio.Markup.Xaml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FormsDesigner.ViewModels;

namespace FormsDesigner.Views;

/// <summary>
/// Where the editor's gestures become document edits.
/// </summary>
/// <remarks>
/// This is the only file that knows both a pointer and a file. The view model above it edits
/// documents and knows nothing about drag thresholds; the editor below it reports gestures and knows
/// nothing about XAML. Keeping the join in one place is what stops either of them growing a little
/// knowledge of the other.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private ToolboxEntry? _dragging;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        DesignEditor surface = this.GetControl<DesignEditor>("Surface");
        ItemsControl toolbox = this.GetControl<ItemsControl>("ToolboxList");

        surface.DesignSelectionChanged += OnDesignSelectionChanged;
        surface.EditCompleted += OnEditCompleted;
        surface.DeleteRequested += OnDeleteRequested;
        surface.ReorderRequested += OnReorderRequested;

        // Drag out of the toolbox, drop onto the surface. Avalonia's own drag-and-drop rather than a
        // hand-rolled pointer dance, so the cursor, the escape key and the drop feedback are the
        // platform's and behave the way every other application does.
        toolbox.AddHandler(PointerPressedEvent, OnToolboxPressed, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(surface, true);

        surface.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        surface.AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is DesignerViewModel designer)
            {
                designer.PickEntryPoint = PickEntryPointAsync;
                designer.AskForName = AskForNameAsync;
                designer.SearchRequested += (_, _) =>
                    this.GetControl<TextBox>("ToolboxSearch").Focus();

                designer.CanvasSelectionRequested += (_, element) => ShowOnCanvas(surface, element);

                // The documented way to clear the canvas: dropping the item selection takes the
                // design targets with it, which is what the frame is drawn from.
                designer.CanvasSelectionCleared += (_, _) => surface.SelectedItems?.Clear();
            }
        };
    }

    private DesignerViewModel? Designer => DataContext as DesignerViewModel;


    /// <summary>
    /// Follows the editor's selection into the document.
    /// </summary>
    /// <remarks>
    /// The editor reports a target as a container and a control inside it. The container is the
    /// form, the control is what the user pointed at, and the object map turns the second into the
    /// element that produced it.
    /// </remarks>
    private void OnDesignSelectionChanged(object? sender, DesignSelectionChangedEventArgs e)
    {
        if (Designer is not { } designer || e.NewPrimary is not { } primary)
        {
            return;
        }

        if (primary.Container.DataContext is FormViewModel form)
        {
            designer.SelectFromCanvas(form, primary.Target);
        }
    }

    /// <summary>
    /// Follows the tree's selection onto the canvas, which is the direction the editor cannot infer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map answers the question the other way round from the selection handler above: there a
    /// control became an element, here an element becomes the control it produced.
    /// </para>
    /// <para>
    /// A window-rooted form needs the second attempt. Its root is a control the canvas cannot host —
    /// that is the whole reason a stand-in exists — so it is nowhere under the editor and cannot be
    /// selected as itself. The card is what stands in for it on screen, so the card is what gets
    /// selected, which is the same answer the canvas gives in the other direction when a click lands
    /// on the stand-in. Without it, picking the root row of a window form did nothing and said
    /// nothing.
    /// </para>
    /// <para>
    /// A row with nothing live behind it at all leaves the canvas as it was, and says so — because a
    /// selection that silently does not happen is indistinguishable from a designer that has stopped
    /// responding. Leaving it is a choice, not a limit: clearing the canvas is
    /// <c>surface.SelectedItems.Clear()</c>, which drops the design targets with it. Keeping the
    /// last selection is the friendlier answer to "that row names nothing you can point at".
    /// </para>
    /// </remarks>
    private void ShowOnCanvas(DesignEditor surface, XamlElement element)
    {
        if (Designer is not { ActiveForm: { } form } designer)
        {
            return;
        }

        if (DesignerViewModel.ControlFor(form, element) is { } control
            && surface.SelectDesignTarget(control))
        {
            return;
        }

        if (ReferenceEquals(element, form.Document?.Root)
            && surface.ContainerFromItem(form) is Control card
            && surface.SelectDesignTarget(card))
        {
            return;
        }

        designer.Log($"! {element.Name} is not on the canvas to select");
    }

    /// <summary>
    /// Writes a finished move or resize into the document.
    /// </summary>
    /// <remarks>
    /// On completion rather than on every delta: a drag produces hundreds of intermediate positions
    /// and only the one the user let go at is a fact about the form. Editing the document per frame
    /// would also rebuild the live tree per frame, which is a designer that fights the mouse.
    /// </remarks>
    private void OnEditCompleted(object? sender, DesignEditCompletedEventArgs e)
    {
        if (Designer is not { } designer)
        {
            return;
        }

        foreach (DesignChange change in e.Changes)
        {
            if (FormOf(change.Target) is { } form)
            {
                designer.WriteGeometry(
                    form,
                    change.Target,
                    moved: e.Kind == DesignEditKind.Move,
                    resized: e.Kind == DesignEditKind.Resize);
            }
        }
    }

    /// <summary>
    /// Writes the colour a picker was left on.
    /// </summary>
    /// <remarks>
    /// On close rather than on change, because a change arrives for every colour the pointer crosses
    /// in the spectrum and each one rebuilds the inspector's rows — which would take away the picker
    /// mid-gesture. The row has been showing the colour all along; this is where it becomes an edit.
    /// </remarks>
    private void OnColourPicked(object? sender, System.EventArgs e)
    {
        if (sender is Flyout { Target.DataContext: PropertyRow row })
        {
            row.CommitColour();
        }
    }

    private void OnDeleteRequested(object? sender, DesignEditorDeleteRequestedEventArgs e)
    {
        if (Designer is not { } designer)
        {
            return;
        }

        foreach (DesignSelectionTarget target in e.Targets)
        {
            if (target.Container.DataContext is FormViewModel form)
            {
                designer.DeleteFromCanvas(form, target.Target);
            }
        }

        // Answering it is what makes it happen: until a subscriber marks the request handled the
        // editor does nothing, because removing a control from the tree is not the editor's to do.
        e.Handled = true;
    }

    /// <summary>
    /// Answers the editor's reorder request, which is what makes the gesture happen at all.
    /// </summary>
    /// <remarks>
    /// The editor refuses to start a drag in a flow layout when nothing is listening — no insertion
    /// point is drawn and no order changes — so this handler is not a refinement of the gesture, it
    /// is the gesture.
    /// </remarks>
    private void OnReorderRequested(object? sender, DesignEditorReorderRequestedEventArgs e)
    {
        if (Designer is not { } designer || FormOf(e.Target) is not { } form)
        {
            return;
        }

        designer.ReorderFromCanvas(form, e.Target, e.Anchor);

        e.Handled = true;
    }

    /// <summary>The form a control on the surface belongs to.</summary>
    private static FormViewModel? FormOf(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent as Control)
        {
            if (current is DesignEditorItem item && item.DataContext is FormViewModel form)
            {
                return form;
            }
        }

        return null;
    }

    private async void OnToolboxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl toolbox
            || !e.GetCurrentPoint(toolbox).Properties.IsLeftButtonPressed
            || (e.Source as Control)?.DataContext is not ToolboxEntry entry)
        {
            return;
        }

        _dragging = entry;

        var data = new DataTransfer();

        data.Add(DataTransferItem.CreateText(entry.Name));

        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _dragging is null ? DragDropEffects.None : DragDropEffects.Copy;

    /// <summary>
    /// Turns a drop into an insert, at the point on the form the pointer was over.
    /// </summary>
    /// <remarks>
    /// The position is asked for in the form's own coordinates rather than the window's, because the
    /// surface is panned and zoomed and a screen point means nothing in a file. Avalonia walks the
    /// whole chain of transforms, which is the part that would otherwise be wrong at every zoom but
    /// one.
    /// </remarks>
    /// <summary>
    /// Drops a toolbox entry onto whatever is under the pointer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved geometrically rather than from <c>e.Source</c>, because a loaded form does not take
    /// input: it must not live its own life under the pointer, so nothing inside it is hit-testable
    /// and the event source is the card, whatever the pointer is actually over. Asking the source
    /// therefore answered "the form" for every drop, and every control landed at the root.
    /// </para>
    /// <para>
    /// This is the same trade the editor makes for selection, and it settles it the same way: a
    /// geometric hit test, which does not care whether a control accepts input. What comes back is
    /// the deepest control the document owns at that point; <c>Drop</c> walks up from it to the
    /// nearest one that can hold a child, so a control lands inside the panel it was dropped on
    /// rather than at the top of the form.
    /// </para>
    /// </remarks>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragging is not { } entry || Designer is not { } designer)
        {
            return;
        }

        DesignEditor editor = this.GetControl<DesignEditor>("Surface");

        if (FormUnder(editor, designer, e.GetPosition(editor)) is not { } form)
        {
            return;
        }

        Point inForm = e.GetPosition(form.Surface);

        designer.Drop(form, entry, DeepestAt(form, inForm), inForm);

        e.Handled = true;
    }

    /// <summary>The form whose card is under a point on the canvas.</summary>
    private static FormViewModel? FormUnder(DesignEditor editor, DesignerViewModel designer, Point at)
    {
        foreach (FormViewModel form in designer.Forms)
        {
            if (editor.ContainerFromItem(form) is Control card
                && card.TranslatePoint(default, editor) is { } origin
                && new Rect(origin, card.Bounds.Size).Contains(at))
            {
                return form;
            }
        }

        return null;
    }

    /// <summary>The smallest control the document owns whose rectangle contains a point.</summary>
    /// <remarks>
    /// By rectangle rather than by rendering, which is the same rule the editor uses to decide what
    /// a click landed on and for the same reason: a panel that paints no background renders nothing
    /// to hit, so a geometric hit test walks straight past the very containers a drop most wants to
    /// find. A form's empty area belongs to the panel that occupies it, and dropping there should
    /// put the control in that panel rather than at the top of the form.
    /// </remarks>
    private static Control? DeepestAt(FormViewModel form, Point at)
    {
        if (form.Objects is not { } map)
        {
            return null;
        }

        Control? best = null;
        int deepest = -1;
        double smallest = double.PositiveInfinity;

        foreach (object produced in map.Objects)
        {
            if (produced is not Control control
                || map.GetElement(control) is null
                || control.TranslatePoint(default, form.Surface) is not { } origin)
            {
                continue;
            }

            var rectangle = new Rect(origin, control.Bounds.Size);

            if (!rectangle.Contains(at))
            {
                continue;
            }

            int depth = DepthIn(control, form.Surface);
            double area = rectangle.Width * rectangle.Height;

            // Deeper wins, and area only settles a tie. A panel that fills its parent has the same
            // rectangle as the parent, so area alone would hand every drop on an empty form to the
            // root — which is the one container least likely to accept it.
            if (depth > deepest || (depth == deepest && area < smallest))
            {
                best = control;
                deepest = depth;
                smallest = area;
            }
        }

        return best;
    }

    /// <summary>How far a control sits below the control hosting the form.</summary>
    private static int DepthIn(Visual control, Visual host)
    {
        int depth = 0;

        for (Visual? current = control; current is not null && current != host; current = current.GetVisualParent())
        {
            depth++;
        }

        return depth;
    }

    private async Task<string?> PickEntryPointAsync()
    {
        System.Collections.Generic.IReadOnlyList<IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a solution or project",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Solutions and projects")
                    {
                        Patterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"],
                    },
                ],
            });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>Asks for a file name, because a designer that cannot name things is a viewer.</summary>
    private async Task<string?> AskForNameAsync(string suggestion)
    {
        var box = new TextBox { Text = suggestion, Width = 260 };
        var ok = new Button { Content = "Create", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var dialog = new Window
        {
            Title = "New form",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "File name" },
                    box,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        string? answer = null;

        ok.Click += (_, _) =>
        {
            answer = box.Text;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);

        return answer;
    }

    /// <summary>Opens the file a tile in the project grid stands for.</summary>
    /// <remarks>
    /// The grid is an <c>ItemsControl</c> of plain borders rather than a <c>ListBox</c>, because the
    /// design draws tiles and not rows, and an `ItemsControl` has no selection to bind an open to.
    /// So the gesture arrives here, which is where every other gesture in this window arrives.
    /// </remarks>
    private void OnProjectFileOpened(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileTile file })
        {
            Designer?.OpenFile(file);
        }
    }

    // -- The window's own chrome ------------------------------------------------------------------
    //
    // `BorderOnly` keeps the frame and drops the caption, so the toolbar is the title bar and these
    // four handlers are what a caption would otherwise have given for free. Dragging is asked of the
    // platform rather than simulated with pointer arithmetic, which is what keeps snapping, the
    // aero-shake gesture and multi-monitor behaviour working the way every other window does.

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A control in the toolbar handles its own press; the empty space between them moves the
        // window. Testing the source for the Border itself would never match — its Grid covers it —
        // so the walk up asks the real question: was anything clickable underneath the pointer?
        if (IsInteractive(e.Source as Visual))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximised();

            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimise(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object? sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Whether the press landed on something in the toolbar that answers presses itself.</summary>
    private static bool IsInteractive(Visual? source)
    {
        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ComboBox or MenuItem)
            {
                return true;
            }

            if (visual is Border { Name: "TitleBar" })
            {
                return false;
            }
        }

        return false;
    }

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
