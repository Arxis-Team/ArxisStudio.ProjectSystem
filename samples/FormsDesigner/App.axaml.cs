using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FormsDesigner.ViewModels;
using FormsDesigner.Views;

namespace FormsDesigner;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Starts the studio, which means the welcome screen unless a project was named on the way in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening straight into an empty canvas answered "which project?" by not asking it. The welcome
    /// screen asks, and the designer is built only once there is an answer — which also keeps MSBuild
    /// out of the path to the first window being on screen.
    /// </para>
    /// <para>
    /// A path on the command line skips it, because somebody who typed a path has already answered.
    /// That is also what makes this application scriptable, which is how its own screenshots are
    /// taken.
    /// </para>
    /// <para>
    /// Shutdown follows the last window rather than the main one: the welcome closes when the
    /// designer opens, and with the default mode that would take the application with it.
    /// </para>
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            string[] args = desktop.Args ?? [];
            int shot = Array.IndexOf(args, "--shot");
            string? shotPath = shot >= 0 && shot + 1 < args.Length ? args[shot + 1] : null;

            // Which of the three the middle shows, so that a screenshot can be of the XAML pane.
            int view = Array.IndexOf(args, "--view");
            string? viewName = view >= 0 && view + 1 < args.Length ? args[view + 1] : null;

            int verify = Array.IndexOf(args, "--verify");
            int stress = Array.IndexOf(args, "--stress");
            int reclaim = Array.IndexOf(args, "--reclaim");

            if (reclaim >= 0 && reclaim + 1 < args.Length)
            {
                var model = new DesignerViewModel();
                var window = new MainWindow { DataContext = model };

                StudioCheck.ReclaimWhenShown(window, model, args[reclaim + 1]);

                desktop.Exit += (_, _) => model.Dispose();
                desktop.MainWindow = window;
            }
            else if (verify >= 0 && verify + 1 < args.Length)
            {
                var model = new DesignerViewModel();
                var window = new MainWindow { DataContext = model };

                StudioCheck.RunWhenShown(window, model, args[verify + 1]);

                desktop.Exit += (_, _) => model.Dispose();
                desktop.MainWindow = window;
            }
            else if (stress >= 0 && stress + 1 < args.Length)
            {
                var model = new DesignerViewModel();
                var window = new MainWindow { DataContext = model };

                StudioCheck.StressWhenShown(window, model, args[stress + 1]);

                desktop.Exit += (_, _) => model.Dispose();
                desktop.MainWindow = window;
            }
            else if (Positional(args) is [{ Length: > 0 } path, .. var rest])
            {
                int active = Array.IndexOf(args, "--active");
                string? activeName = active >= 0 && active + 1 < args.Length ? args[active + 1] : null;

                desktop.MainWindow = Designer(desktop, path, rest, activeName, shotPath, viewName);
            }
            else
            {
                var welcome = new WelcomeWindow { DataContext = new WelcomeViewModel() };

                if (shotPath is { Length: > 0 })
                {
                    WindowShot.TakeWhenShown(welcome, shotPath);
                }

                ((WelcomeViewModel)welcome.DataContext).ProjectChosen += (_, chosen) =>
                {
                    MainWindow designer = Designer(
                        desktop, chosen, forms: [], active: null, shotPath: null, viewName: null);

                    desktop.MainWindow = designer;

                    designer.Show();
                    welcome.Close();
                };

                desktop.MainWindow = welcome;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds the designer window around a project, and disposes it when the studio exits.</summary>
    private static MainWindow Designer(
        IClassicDesktopStyleApplicationLifetime desktop,
        string path,
        IReadOnlyList<string> forms,
        string? active,
        string? shotPath,
        string? viewName)
    {
        var model = new DesignerViewModel();
        var window = new MainWindow { DataContext = model };

        if (viewName is { Length: > 0 })
        {
            model.View = viewName.ToUpperInvariant() switch
            {
                "XAML" => DocumentView.Xaml,
                "SPLIT" => DocumentView.Split,
                _ => DocumentView.Design,
            };
        }

        if (shotPath is { Length: > 0 })
        {
            WindowShot.TakeAfterLoad(window, model, shotPath);
        }

        model.OpenAtStartup(path, forms, active);

        desktop.Exit += (_, _) => model.Dispose();

        return window;
    }

    /// <summary>The arguments that are not a switch and are not a switch's value.</summary>
    /// <remarks>
    /// Every switch this sample takes takes a value: `--shot`, `--verify`, `--stress`,
    /// `--reclaim`, `--view` and `--active`.
    /// </remarks>
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
