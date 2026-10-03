using Avalonia.Controls;
using Avalonia.Metadata;

[assembly: XmlnsDefinition(
    "https://github.com/arxis-team/projectsystem/package-fixtures",
    "ArxisStudio.ProjectSystem.Markup.Xaml.PackageFixtures")]

namespace ArxisStudio.ProjectSystem.Markup.Xaml.PackageFixtures;

/// <summary>
/// A package's control, named in a document by the package's XAML namespace.
/// </summary>
public sealed class PackageGauge : Control
{
}
