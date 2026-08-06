using System;
using System.Collections.Generic;
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

            // Positional arguments are whatever is left once every switch and the value it takes has
            // been removed. Skipping only the switches is not enough: `--shot out.png` then donates
            // `out.png` to the form name, and the designer reports that no form is called that --
            // which is true, and entirely the tool's own doing.
            if (Positional(args) is [{ Length: > 0 } path, .. var rest])
            {
                designer.OpenAtStartup(path, rest.FirstOrDefault());
            }

            desktop.Exit += (_, _) => designer.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The arguments that are not a switch and are not a switch's value.</summary>
    /// <remarks>Every switch this sample takes takes a value; there is one, and it is `--shot`.</remarks>
    private static string[] Positional(string[] args)
    {
        var positional = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                index++;

                continue;
            }

            positional.Add(args[index]);
        }

        return [.. positional];
    }
}
