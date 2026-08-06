using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FormsDesigner.ViewModels;
using FormsDesigner.Views;

namespace FormsDesigner;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var designer = new DesignerViewModel();

            desktop.MainWindow = new MainWindow { DataContext = designer };

            var window = (MainWindow)desktop.MainWindow;

            string[] args = desktop.Args ?? [];
            int shot = Array.IndexOf(args, "--shot");

            if (shot >= 0 && shot + 1 < args.Length)
            {
                WindowShot.TakeAfterLoad(window, designer, args[shot + 1]);
            }

            if (args is [{ Length: > 0 } path, .. var rest] && path != "--shot")
            {
                designer.OpenAtStartup(path, rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)));
            }

            desktop.Exit += (_, _) => designer.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
