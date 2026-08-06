using System.Threading.Tasks;
using ArxisStudio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragging is not { } entry || Designer is not { } designer)
        {
            return;
        }

        Control? over = e.Source as Control;

        if (over is null || FormOf(over) is not { } form || form.Surface is not { } surface)
        {
            return;
        }

        // Against the surface, which is what is on screen. For a window-rooted form the root is not
        // in the visual tree at all, and asking a control that was never shown where a point is
        // gives an answer about nothing.
        designer.Drop(form, entry, over, e.GetPosition(surface));

        e.Handled = true;
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
}
