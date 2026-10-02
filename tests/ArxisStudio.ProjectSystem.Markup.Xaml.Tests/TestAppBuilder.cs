using ArxisStudio.ProjectSystem.Markup.Xaml.Tests;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// The headless application every <c>[AvaloniaFact]</c> here runs in.
/// </summary>
/// <remarks>
/// A generation is proven gone only once the user interface has let go of what it drew, and a window
/// is only a window once a windowing platform keeps it — so the tests that design against a project's
/// own controls need a real Avalonia thread, which the headless platform gives without a display.
/// </remarks>
public static class TestAppBuilder
{
    /// <summary>Builds the headless application.</summary>
    /// <returns>The builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
