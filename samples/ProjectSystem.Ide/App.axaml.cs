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
            // Exit and not ShutdownRequested. ShutdownRequested is a question -- a subscriber may
            // set Cancel and the application carries on -- so disposing there would leave a live
            // window in front of a view model whose cancellation source, feed and HttpClient are
            // all gone. Exit is the answer, and it is raised once the decision is final.
            desktop.Exit += (_, _) => workspace.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
