using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProjectSystem.Ide.ViewModels;
using ProjectSystem.Ide.Views;

namespace ProjectSystem.Ide;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var workspace = new IdeViewModel();

            desktop.MainWindow = new MainWindow { DataContext = workspace };

            if (desktop.Args is [{ Length: > 0 } path, ..])
            {
                workspace.OpenAtStartup(path);
            }

            // The workspace owns a provider, a file watcher and a collectible load context. Letting
            // the process exit without disposing it would leave a build running and a watcher
            // holding directory handles.
            desktop.ShutdownRequested += (_, _) => workspace.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
