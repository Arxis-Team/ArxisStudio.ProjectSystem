using System.Collections.Frozen;
using System.Collections.Generic;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// The MSBuild names this package knows about, spelled once.
/// </summary>
internal static class MSBuildWellKnown
{
    /// <summary>Item types that become typed references rather than plain items.</summary>
    internal const string ProjectReference = "ProjectReference";

    /// <summary>The declared NuGet package item type.</summary>
    internal const string PackageReference = "PackageReference";

    /// <summary>The shared-framework item type.</summary>
    internal const string FrameworkReference = "FrameworkReference";

    /// <summary>The direct assembly reference item type.</summary>
    internal const string Reference = "Reference";

    /// <summary>The analyzer assembly item type.</summary>
    internal const string Analyzer = "Analyzer";

    /// <summary>
    /// Item types the translator turns into typed references, so they are not repeated as plain
    /// items. Everything else becomes a <see cref="ProjectItem"/>.
    /// </summary>
    internal static FrozenSet<string> ReferenceItemTypes { get; } = new[]
    {
        ProjectReference, PackageReference, FrameworkReference, Reference, Analyzer,
    }.ToFrozenSet(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The evaluated properties surfaced on a snapshot.
    /// </summary>
    /// <remarks>
    /// A curated list rather than everything MSBuild evaluated, and the reason is arithmetic: a
    /// full evaluation produces on the order of a thousand properties, most of them internal
    /// bookkeeping, and carrying all of them on every project of every snapshot costs megabytes to
    /// answer a question nobody asked. If a consumer turns up needing more, widening this is a
    /// small change with a name attached to it; guessing now is not.
    /// </remarks>
    internal static FrozenSet<string> SurfacedProperties { get; } = new[]
    {
        "AssemblyName", "RootNamespace", "OutputType", "ProjectGuid",
        "TargetFramework", "TargetFrameworks", "TargetFrameworkIdentifier", "TargetFrameworkVersion",
        "Configuration", "Configurations", "Platform", "Platforms", "PlatformTarget",
        "OutputPath", "TargetPath", "TargetFileName", "TargetName", "TargetExt",
        "BaseIntermediateOutputPath", "IntermediateOutputPath",
        "LangVersion", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors",
        "UseWPF", "UseWindowsForms", "IsPackable", "IsTestProject",
        "DocumentationFile", "AssemblyVersion", "FileVersion", "Version",
        "MSBuildProjectName", "MSBuildProjectDirectory", "MSBuildProjectExtension",
    }.ToFrozenSet(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Language labels, keyed by project file extension.</summary>
    /// <remarks>
    /// Derived from the extension rather than from MSBuild's <c>Language</c> property, which not
    /// every SDK sets. The extension is what decided which SDK ran in the first place.
    /// </remarks>
    internal static FrozenDictionary<string, string> LanguagesByExtension { get; } =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            [".csproj"] = "C#",
            [".fsproj"] = "F#",
            [".vbproj"] = "Visual Basic",
            [".vcxproj"] = "C++",
        }.ToFrozenDictionary(System.StringComparer.OrdinalIgnoreCase);
}
