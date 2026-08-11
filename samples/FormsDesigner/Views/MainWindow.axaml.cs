using System;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio;
using ArxisStudio.Markup.Xaml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

        Activated += (_, _) => (DataContext as DesignerViewModel)?.OnStudioActivated();

        DesignEditor surface = this.GetControl<DesignEditor>("Surface");
        AvaloniaEdit.TextEditor markup = this.GetControl<AvaloniaEdit.TextEditor>("XamlView");

        // The editor's own XML rules, which is what "colour a tag differently from its attributes"
        // means without this file inventing a second definition of XAML.
        markup.SyntaxHighlighting =
            AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("XML");

        // Painted once the window is up, not here: a window that is not attached yet finds none of
        // the application's resources, so every colour asked for in a constructor comes back unset.
        Opened += (_, _) => PaintMarkup(markup);

        // And again when the palette changes, because the colours come out of it.
        ActualThemeVariantChanged += (_, _) => PaintMarkup(markup);
        ItemsControl toolbox = this.GetControl<ItemsControl>("ToolboxList");

        surface.DesignSelectionChanged += OnDesignSelectionChanged;
        surface.EditCompleted += OnEditCompleted;
        surface.DeleteRequested += OnDeleteRequested;
        surface.ContextMenuRequesting += OnContextMenuRequesting;

        // A right press selects the row under the pointer before its menu opens, or the menu would
        // act on whatever happened to be selected from before.
        this.GetControl<ListBox>("HierarchyTree").AddHandler(
            PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel);
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
                // The pane follows the document rather than binding to it: TextEditor keeps its text
                // in a document of its own, and assigning only what changed keeps the caret and the
                // scroll position where the reader left them.
                ShowMarkup(markup, designer.DocumentText);

                designer.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName == nameof(DesignerViewModel.DocumentText))
                    {
                        ShowMarkup(markup, designer.DocumentText);
                    }
                };

                designer.PickEntryPoint = PickEntryPointAsync;
                designer.AskForNewForm = AskForNewFormAsync;
                designer.AskToConfirm = AskToConfirmAsync;
                designer.AskToSave = AskToSaveAsync;

                // The Unity rule: code edited elsewhere is applied the moment this window is the
                // one being looked at. The model owns whether a reload is due; the window only
                // says when somebody came back, and whether they are here right now.
                designer.IsStudioActive = () => IsActive;

                designer.ZoomToFitRequested += (_, _) => OnZoomToFit(this, new RoutedEventArgs());

                // The centre's columns follow the view. Re-asserted on every change rather than
                // bound, because the splitter writes local widths when it is dragged, and a local
                // value outlives any binding — switching Design → XAML → Split would otherwise
                // keep whatever widths the last drag happened to leave.
                designer.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName == nameof(DesignerViewModel.View))
                    {
                        ApplyViewColumns(designer.View);
                    }
                };

                ApplyViewColumns(designer.View);
                designer.SearchRequested += (_, _) =>
                    this.GetControl<TextBox>("ToolboxSearch").Focus();

                designer.CanvasSelectionRequested += (_, element) => ShowOnCanvas(surface, element);

                // The documented way to clear the canvas: dropping the item selection takes the
                // design targets with it, which is what the frame is drawn from.
                designer.CanvasSelectionCleared += (_, _) => surface.SelectedItems?.Clear();

                // The clipboard belongs to the top level, so the window is what reaches for it.
                // Avalonia 12 puts data transfers on it rather than strings: an item carries the
                // text and the clipboard owns it once it has been handed over.
                designer.PutOnClipboard = async markup =>
                {
                    if (Clipboard is { } clipboard)
                    {
                        var transfer = new DataTransfer();

                        transfer.Add(DataTransferItem.Create(DataFormat.Text, markup));

                        await clipboard.SetDataAsync(transfer);
                    }
                };

                designer.TakeFromClipboard = async () =>
                {
                    if (Clipboard is not { } clipboard)
                    {
                        return null;
                    }

                    // Returned rather than lent: what comes off the clipboard is the caller's to
                    // dispose, which is the opposite of what goes on to it.
                    using IAsyncDataTransfer? transfer = await clipboard.TryGetDataAsync();

                    return transfer is null ? null : await transfer.TryGetTextAsync();
                };
            }
        };
    }

    private DesignerViewModel? Designer => DataContext as DesignerViewModel;

    /// <summary>
    /// Deals the centre's columns for a view: all canvas, all editor, or a split.
    /// </summary>
    /// <remarks>
    /// The editor used to keep a fixed 520 pixels at the window's right edge whatever the view,
    /// which in XAML-only left the canvas's empty star column holding the whole middle of the
    /// window — a void where the editor should have been.
    /// </remarks>
    private void ApplyViewColumns(DocumentView view)
    {
        ColumnDefinitions columns = this.GetControl<Grid>("CentreColumns").ColumnDefinitions;

        (columns[0].Width, columns[2].Width) = view switch
        {
            DocumentView.Xaml => (new GridLength(0), new GridLength(1, GridUnitType.Star)),
            DocumentView.Split => (new GridLength(1, GridUnitType.Star), new GridLength(520)),
            _ => (new GridLength(1, GridUnitType.Star), new GridLength(0)),
        };
    }

    /// <summary>
    /// Repaints the XAML pane's highlighting in the design's own colours.
    /// </summary>
    /// <remarks>
    /// The editor's XML rules are written for a white page: on this background an attribute value
    /// came out dark red on near-black and a tag name dark blue, which is a pane nobody can read.
    /// The rules are kept — what a tag is and where a value starts is exactly what they know — and
    /// only the colours are replaced, taken from the same tokens as the rest of the window, so the
    /// pane follows the light and dark palettes with everything else.
    /// </remarks>
    private void PaintMarkup(AvaloniaEdit.TextEditor editor)
    {
        if (editor.SyntaxHighlighting is not { } rules)
        {
            return;
        }

        Paint("XmlTag", "Pur");
        Paint("AttributeName", "Acc");
        Paint("AttributeValue", "Grn");
        Paint("Comment", "Fg3");
        Paint("XmlDeclaration", "Fg2");
        Paint("DocType", "Fg2");
        Paint("CData", "Yel");
        Paint("Entity", "Yel");
        Paint("BrokenEntity", "Red");

        // Re-assigned so the view re-reads the colours it had already cached.
        editor.SyntaxHighlighting = rules;
        editor.TextArea.TextView.Redraw();

        void Paint(string token, string key)
        {
            // Asked with the variant, because the palette lives in theme dictionaries: a lookup
            // that does not say which variant it wants finds nothing at all in one.
            if (rules.GetNamedColor(token) is not { } colour
                || !this.TryFindResource(key, ActualThemeVariant, out object? resource)
                || resource is not ISolidColorBrush brush)
            {
                return;
            }

            colour.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(brush.Color);
        }
    }

    /// <summary>Puts the document in the XAML pane, and only when it is not already there.</summary>
    private static void ShowMarkup(AvaloniaEdit.TextEditor editor, string markup)
    {
        if (!string.Equals(editor.Text, markup, StringComparison.Ordinal))
        {
            editor.Text = markup;
        }
    }


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
        if (sender is not Flyout { Target.DataContext: PropertyRow row } || Designer is not { } designer)
        {
            return;
        }

        // Only if this row is still the one the inspector is showing. A flyout also closes because
        // the rows were rebuilt under it — any edit does that, and the row then holds an element the
        // document has replaced, whose spans point into text that is no longer there. Writing
        // through it would address the wrong document; skipping is the only correct answer left.
        if (designer.Properties.Contains(row))
        {
            row.CommitColour();
        }
    }

    /// <summary>
    /// Fills the canvas's context menu with what this designer can do to a control.
    /// </summary>
    /// <remarks>
    /// The editor asks and the host answers, which is the right way round: the editor knows a right
    /// click happened over a target and nothing about clipboards or documents. Availability is asked
    /// of the commands themselves, so the menu cannot offer a paste when there is nothing to paste
    /// into.
    /// </remarks>
    private void OnContextMenuRequesting(object? sender, DesignEditorContextRequestingEventArgs e)
    {
        if (Designer is not { } designer)
        {
            return;
        }

        e.Actions =
        [
            Action("copy", "Копировать", "Ctrl+C", designer.CopyCommand, 0),
            Action("cut", "Вырезать", "Ctrl+X", designer.CutCommand, 1),
            Action("paste", "Вставить", "Ctrl+V", designer.PasteCommand, 2),
            Action("duplicate", "Дублировать", "Ctrl+D", designer.DuplicateCommand, 3),
            new DesignEditorContextAction { Id = "-", IsSeparator = true, Group = "edit", Order = 4 },
            Action("undo", "Отменить", "Ctrl+Z", designer.UndoCommand, 5),
            Action("redo", "Вернуть", "Ctrl+Y", designer.RedoCommand, 6),
            new DesignEditorContextAction { Id = "--", IsSeparator = true, Group = "edit", Order = 7 },
            Action("up", "Выше", "Alt+Up", designer.MoveUpCommand, 8),
            Action("down", "Ниже", "Alt+Down", designer.MoveDownCommand, 9),
            new DesignEditorContextAction
            {
                Id = "wrap",
                Header = "Обернуть в",
                Group = "edit",
                Order = 10,
                Items =
                [
                    .. DesignerViewModel.WrapContainers.Select(container =>
                        new DesignEditorContextAction
                        {
                            Id = "wrap:" + container,
                            Header = container,
                            Command = new RelayCommand(() => designer.Wrap(container)),
                        }),
                ],
            },
            new DesignEditorContextAction
            {
                Id = "unwrap",
                Header = "Развернуть",
                Group = "edit",
                Order = 11,
                Command = new RelayCommand(designer.Unwrap),
            },
            new DesignEditorContextAction { Id = "---", IsSeparator = true, Group = "edit", Order = 12 },
            Action("delete", "Удалить", "Del", designer.DeleteSelectedCommand, 13),
        ];

        static DesignEditorContextAction Action(
            string id, string header, string gesture, FormsDesigner.ViewModels.RelayCommand command, int order) =>
            new()
            {
                Id = id,
                Header = $"{header}   {gesture}",
                Group = "edit",
                Order = order,
                Command = command,
                IsEnabled = command.CanExecute(null),
            };
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

    /// <summary>
    /// Asks what to create and what to call it.
    /// </summary>
    /// <remarks>
    /// Two questions in one dialog because they are one decision: a window and a user control are
    /// different things and are named differently, so the suggestion follows the choice until
    /// somebody types over it. The dialog before this one asked only for a name and made a user
    /// control whatever the answer was — which is how a control came to be called NewForm.
    /// </remarks>
    private async Task<NewFormRequest?> AskForNewFormAsync(NewFormRequest suggested)
    {
        var window = new RadioButton { Content = "Окно", GroupName = "kind", IsChecked = true };
        var control = new RadioButton { Content = "UserControl", GroupName = "kind" };
        var name = new TextBox { Text = suggested.Name, Width = 300 };

        var create = new Button { Content = "Создать", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };

        var dialog = new Window
        {
            Title = "Новая форма",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Что создать" },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 16,
                        Children = { window, control },
                    },
                    new TextBlock { Text = "Имя" },
                    name,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { create, cancel },
                    },
                },
            },
        };

        // The suggestion follows the choice, and stops following it the moment somebody types.
        var typed = false;

        name.TextChanged += (_, _) => typed = name.Text is not ("NewWindow" or "NewUserControl");

        window.IsCheckedChanged += (_, _) =>
        {
            if (!typed && window.IsChecked == true)
            {
                name.Text = "NewWindow";
            }
        };

        control.IsCheckedChanged += (_, _) =>
        {
            if (!typed && control.IsChecked == true)
            {
                name.Text = "NewUserControl";
            }
        };

        NewFormRequest? answer = null;

        create.Click += (_, _) =>
        {
            answer = new NewFormRequest(
                name.Text ?? string.Empty,
                control.IsChecked == true ? NewFormKind.UserControl : NewFormKind.Window);

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
    /// <summary>A press on a tab makes that form the one being edited.</summary>
    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: FormViewModel form } && Designer is { } designer)
        {
            designer.ActiveForm = form;
        }
    }

    /// <summary>And the cross on it closes that form, whether or not it is the active one.</summary>
    private void OnTabClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FormViewModel form } && Designer is { } designer)
        {
            designer.CloseForm(form, ask: true);
        }
    }

    /// <summary>Selects the row under a right press, so its menu acts on it.</summary>
    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox tree
            || !e.GetCurrentPoint(tree).Properties.IsRightButtonPressed)
        {
            return;
        }

        for (Visual? current = e.Source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is ListBoxItem { DataContext: HierarchyRow row } && Designer is { } designer)
            {
                designer.SelectedHierarchyRow = row;

                return;
            }
        }
    }

    /// <summary>Wraps the selection in the container the menu item names.</summary>
    private void OnWrap(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string container })
        {
            Designer?.Wrap(container);
        }
    }

    private void OnUnwrap(object? sender, RoutedEventArgs e) => Designer?.Unwrap();

    /// <summary>Opens the tree's filter and puts the caret in it.</summary>
    private void OnFindInTree(object? sender, RoutedEventArgs e)
    {
        if (Designer is not { } designer)
        {
            return;
        }

        designer.IsHierarchySearchOpen = !designer.IsHierarchySearchOpen;

        if (designer.IsHierarchySearchOpen)
        {
            this.GetControl<TextBox>("TreeSearch").Focus();
        }
    }

    /// <summary>
    /// Fits the form to the window.
    /// </summary>
    /// <remarks>
    /// The zoom that makes the card fit with a margin around it, and then the viewport moved onto
    /// it. Worked out here rather than in the view model because it is a question about a viewport:
    /// the editor knows how big it is on screen and the form knows how big it is in its own
    /// coordinates, and nothing else needs to know either.
    /// </remarks>
    private void OnZoomToFit(object? sender, RoutedEventArgs e)
    {
        if (Designer is not { ActiveForm: { } form } designer)
        {
            return;
        }

        DesignEditor surface = this.GetControl<DesignEditor>("Surface");

        double width = form.Width;
        double height = form.Height;

        if (width <= 0 || height <= 0 || surface.Bounds.Width <= 0 || surface.Bounds.Height <= 0)
        {
            return;
        }

        // A tenth of the smaller side as breathing room, and never magnified past life size: a form
        // smaller than the window is easier to work on at 100% than blown up to fill it.
        double fit = Math.Min(
            (surface.Bounds.Width * 0.9) / width,
            (surface.Bounds.Height * 0.9) / height);

        designer.Zoom = Math.Clamp(fit, 0.1, 1);

        surface.CenterOnSelection();
    }

    /// <summary>
    /// Asks what to do with a form that is about to be closed with unsaved edits.
    /// </summary>
    /// <remarks>
    /// Three answers, because two of them are wrong on their own: a dialog without Cancel makes the
    /// cross on a tab dangerous, and one without Discard makes it impossible to throw away an
    /// experiment. Cancel is the default, as it is for every question that can lose work.
    /// </remarks>
    private async Task<DesignerViewModel.SaveAnswer> AskToSaveAsync(string name)
    {
        var save = new Button { Content = "Сохранить" };
        var discard = new Button { Content = "Не сохранять" };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = true };

        var dialog = new Window
        {
            Title = "Несохранённые изменения",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"В {name} есть правки, которых нет в файле.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { save, discard, cancel },
                    },
                },
            },
        };

        DesignerViewModel.SaveAnswer answer = DesignerViewModel.SaveAnswer.Cancel;

        save.Click += (_, _) =>
        {
            answer = DesignerViewModel.SaveAnswer.Save;
            dialog.Close();
        };

        discard.Click += (_, _) =>
        {
            answer = DesignerViewModel.SaveAnswer.Discard;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);

        return answer;
    }

    /// <summary>Enter writes the value, by leaving the field the way a click elsewhere would.</summary>
    /// <remarks>
    /// The binding commits on lost focus, so this does not need to know about bindings: moving the
    /// focus is the same event, and it leaves the caret somewhere a person expects after Enter.
    /// </remarks>
    private void OnPropertyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter && sender is Control field)
        {
            field.Focus(NavigationMethod.Unspecified);

            this.GetControl<DesignEditor>("Surface").Focus();

            e.Handled = true;
        }
    }

    private void OnProjectFileMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileTile file })
        {
            Designer?.OpenFile(file);
        }
    }

    private void OnProjectFileDeleted(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileTile file })
        {
            Designer?.DeleteFile(file);
        }
    }

    /// <summary>
    /// Asks a yes-or-no question before something is destroyed.
    /// </summary>
    /// <remarks>
    /// Cancel is the default button, because the answer to a question nobody read should be the one
    /// that changes nothing.
    /// </remarks>
    private async Task<bool> AskToConfirmAsync(string title, string question)
    {
        var yes = new Button { Content = "Удалить" };
        var no = new Button { Content = "Отмена", IsCancel = true, IsDefault = true };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { yes, no },
                    },
                },
            },
        };

        var answer = false;

        yes.Click += (_, _) =>
        {
            answer = true;
            dialog.Close();
        };

        no.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);

        return answer;
    }

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
