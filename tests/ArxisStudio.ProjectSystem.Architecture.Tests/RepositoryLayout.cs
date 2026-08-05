using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// Reads the declarative project graph straight from the <c>.csproj</c> files.
/// Compiled assembly metadata only records references the compiler actually kept,
/// so the source files are the authoritative statement of intended dependencies.
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>The provider-neutral core.</summary>
    public const string CorePackage = "ArxisStudio.ProjectSystem";

    /// <summary>The MSBuild provider.</summary>
    public const string MSBuildPackage = "ArxisStudio.ProjectSystem.MSBuild";

    /// <summary>The shipping packages, ordered from the bottom of the stack upwards.</summary>
    public static IReadOnlyList<string> Packages { get; } = new[] { CorePackage, MSBuildPackage };

    public static string RepositoryRoot { get; } = ResolveRepositoryRoot();

    /// <summary>Project names a package declares a <c>ProjectReference</c> to.</summary>
    public static IReadOnlySet<string> ProjectReferencesOf(string package) =>
        ReadReferences(package, "ProjectReference", static value =>
            Path.GetFileNameWithoutExtension(value.Replace('\\', '/')));

    /// <summary>
    /// NuGet package ids a package ends up referencing, its own project file and the shared
    /// <c>src/Directory.Build.props</c> together.
    /// </summary>
    /// <remarks>
    /// A reference declared once for every shipping project is still a reference each of them
    /// has. Reading only the project file would miss both what they share and what could be
    /// smuggled in through it.
    /// </remarks>
    public static IReadOnlySet<string> PackageReferencesOf(string package) =>
        new HashSet<string>(
            ReadReferences(package, "PackageReference", static value => value)
                .Concat(ReadFrom(
                    Path.Combine(RepositoryRoot, "src", "Directory.Build.props"),
                    "PackageReference",
                    static value => value)),
            StringComparer.Ordinal);

    /// <summary>Loads a shipping assembly from the test output directory.</summary>
    public static Assembly LoadAssembly(string package) => Assembly.Load(new AssemblyName(package));

    /// <summary>The path of a shipping project's file.</summary>
    public static string ProjectFileOf(string package) =>
        Path.Combine(RepositoryRoot, "src", package, package + ".csproj");

    /// <summary>Every C# source file of the shipping package.</summary>
    public static IReadOnlyList<string> CoreSourceFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", CorePackage), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>The XML documentation the shipping package generates, beside its assembly.</summary>
    public static string DocumentationFileOf(string package) =>
        Path.ChangeExtension(LoadAssembly(package).Location, ".xml");

    private static IReadOnlySet<string> ReadReferences(
        string package,
        string itemName,
        Func<string, string> select)
    {
        string projectPath = ProjectFileOf(package);

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException(
                $"Project file for package '{package}' was not found. The repository layout changed.",
                projectPath);
        }

        return ReadFrom(projectPath, itemName, select);
    }

    private static IReadOnlySet<string> ReadFrom(string path, string itemName, Func<string, string> select) =>
        !File.Exists(path)
            ? new HashSet<string>(StringComparer.Ordinal)
            : XDocument.Load(path)
                .Descendants(itemName)
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(value => select(value!))
                .ToHashSet(StringComparer.Ordinal);

    private static string ResolveRepositoryRoot()
    {
        string? root = typeof(RepositoryLayout).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute =>
                string.Equals(attribute.Key, "RepositoryRoot", StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException(
                "The RepositoryRoot assembly metadata is missing or does not point at an existing directory. " +
                "It is supplied by ArxisStudio.ProjectSystem.Architecture.Tests.csproj.");
        }

        return root;
    }
}
