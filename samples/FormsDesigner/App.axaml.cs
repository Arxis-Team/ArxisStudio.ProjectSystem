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

            if (desktop.Args is [{ Length: > 0 } path, .. var rest])
            {
                designer.OpenAtStartup(path, rest.FirstOrDefault());
            }

            desktop.Exit += (_, _) => designer.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
