using Avalonia;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>
/// A program's application class, as an <c>App.axaml</c> names it with <c>x:Class</c>.
/// </summary>
/// <remarks>
/// It resolves, so a load that constructs what <c>x:Class</c> names would build this rather than a plain
/// <see cref="Application"/> — which is how a test tells a host that loads the document as written from
/// one that starts the program inside the designer.
/// </remarks>
public class FixtureApplication : Application
{
}
