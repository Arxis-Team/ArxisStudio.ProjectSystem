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

            if (desktop.Args is [{ Length: > 0 } path, .. var rest])
            {
                workspace.OpenAtStartup(path, System.Array.IndexOf(rest, "--run") >= 0);
            }

            // The workspace owns a provider, a file watcher, a package feed, an HttpClient and a
            // collectible load context. Letting the process exit without disposing it would leave a
            // build running and a watcher holding directory handles.
            //
            // Both events, because neither alone covers every way this ends: ShutdownRequested is
            // the one that can be cancelled and does not arrive on an explicit Environment.Exit,
            // and Exit is raised last. Disposal is idempotent, so arriving twice is fine.
            desktop.ShutdownRequested += (_, _) => workspace.Dispose();
            desktop.Exit += (_, _) => workspace.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
