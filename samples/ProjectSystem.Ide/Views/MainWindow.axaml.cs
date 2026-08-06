using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProjectSystem.Ide.ViewModels;

namespace ProjectSystem.Ide.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The view model does not know what a window is; picking a file needs one, so the window
        // supplies the picking and nothing else.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is IdeViewModel ide)
            {
                ide.PickEntryPoint = PickAsync;
            }
        };
    }

    private async Task<string?> PickAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open a solution or a project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Solutions and projects")
                {
                    Patterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"],
                },
            ],
        };

        System.Collections.Generic.IReadOnlyList<IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(options);

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
