using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FormsDesigner.ViewModels;

namespace FormsDesigner.Views;

/// <summary>
/// The window the studio starts in, and the dialogs it needs to answer for a project.
/// </summary>
/// <remarks>
/// The same division as the designer's own window: the view model decides what happens and this
/// file knows what a file picker is. Dialogs are built here in code rather than as markup of their
/// own, because each is a handful of controls and a window per question would be four more files
/// to keep in step with the design.
/// </remarks>
public sealed partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is WelcomeViewModel welcome)
            {
                welcome.PickProjectFile = PickProjectFileAsync;
                welcome.PickFolder = PickFolderAsync;
                welcome.AskForNewProject = AskForNewProjectAsync;
                welcome.AskForText = AskForTextAsync;
            }
        };
    }

    private WelcomeViewModel? Welcome => DataContext as WelcomeViewModel;

    private void OnSectionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string section } && Welcome is { } welcome)
        {
            welcome.Section = section;
        }
    }

    private void OnRecentOpened(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentProject project } && Welcome is { } welcome)
        {
            welcome.Open(project);
        }
    }

    private void OnRecentForgotten(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentProject project } && Welcome is { } welcome)
        {
            welcome.Forget(project);
        }
    }

    private void OnTemplateChosen(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProjectTemplate template } && Welcome is { } welcome)
        {
            welcome.Start(template);
        }
    }

    /// <summary>Opens a link in whatever the system opens links with.</summary>
    private void OnLinkPressed(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { Tag: string url })
        {
            return;
        }

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A machine with nothing registered for http is a machine where this does nothing.
        }
    }

    private async Task<string?> PickProjectFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Открыть решение или проект",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Решения и проекты")
                    {
                        Patterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"],
                    },
                ],
            });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <summary>Where a new project goes when nobody has said otherwise.</summary>
    private static string DefaultLocation => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ArxisStudio");

    /// <summary>
    /// Asks for the three things a new project needs, and refuses to create one it cannot name.
    /// </summary>
    /// <remarks>
    /// The name is checked as it is typed rather than after Create is pressed: a project name is a
    /// namespace and a directory as well as a label, and finding that out from a compiler error two
    /// minutes later is the kind of thing a studio is supposed to prevent.
    /// </remarks>
    private async Task<NewProjectRequest?> AskForNewProjectAsync(ProjectTemplate chosen)
    {
        var name = new TextBox { Text = "MyApplication", Width = 320 };
        var location = new TextBox { Text = DefaultLocation, Width = 320 };
        var browse = new Button { Content = "…", Width = 32, Height = 30 };

        var templates = new ComboBox
        {
            ItemsSource = ProjectScaffold.Templates,
            SelectedItem = chosen,
            Width = 320,
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ProjectTemplate.Name)),
        };

        var problem = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.IndianRed,
            FontSize = 11,
            IsVisible = false,
        };

        var create = new Button { Content = "Создать", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };

        var dialog = new Window
        {
            Title = "Новый проект",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Название" },
                    name,
                    new TextBlock { Text = "Расположение" },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children = { location, browse },
                    },
                    new TextBlock { Text = "Шаблон" },
                    templates,
                    problem,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { create, cancel },
                    },
                },
            },
        };

        NewProjectRequest? answer = null;

        name.TextChanged += (_, _) =>
        {
            bool usable = ProjectScaffold.IsUsableName(name.Text ?? string.Empty);

            problem.Text = "Имя начинается с буквы и состоит из букв, цифр, точек и подчёркиваний.";
            problem.IsVisible = !usable;
            create.IsEnabled = usable;
        };

        browse.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Где создать проект") is { Length: > 0 } picked)
            {
                location.Text = picked;
            }
        };

        create.Click += (_, _) =>
        {
            if (!ProjectScaffold.IsUsableName(name.Text ?? string.Empty))
            {
                return;
            }

            answer = new NewProjectRequest(
                name.Text!.Trim(),
                location.Text is { Length: > 0 } where ? where : DefaultLocation,
                templates.SelectedItem as ProjectTemplate ?? ProjectScaffold.Templates[0]);

            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);

        return answer;
    }

    private async Task<string?> AskForTextAsync(string title, string watermark)
    {
        var box = new TextBox { PlaceholderText = watermark, Width = 380 };
        var ok = new Button { Content = "Дальше", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Right,
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
