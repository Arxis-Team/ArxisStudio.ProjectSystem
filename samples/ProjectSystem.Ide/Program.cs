using System;
using Avalonia;

namespace ProjectSystem.Ide;

internal static class Program
{
    /// <summary>
    /// Starts the application.
    /// </summary>
    /// <remarks>
    /// Nothing registers MSBuild here. The provider does it on first use, and doing it earlier would
    /// mean naming a <c>Microsoft.Build</c> type in a method the runtime enters before the locator
    /// has run — which is the one mistake the whole load-order discipline in
    /// <c>ArxisStudio.ProjectSystem.MSBuild</c> exists to prevent.
    /// </remarks>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
