using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// Guards the package a consumer would actually restore: its metadata, the files it carries,
/// and the promise that nothing but the core is published.
/// </summary>
public sealed class PackagingTests
{
    private static string SharedProps => Path.Combine(RepositoryLayout.RepositoryRoot, "src", "Directory.Build.props");

    /// <summary>
    /// Every property a preview package needs to be inspectable and debuggable. A missing one is
    /// not noticed by the build — it is noticed by whoever tries to step into the package.
    /// </summary>
    [Theory]
    [InlineData("IsPackable")]
    [InlineData("GenerateDocumentationFile")]
    [InlineData("IncludeSymbols")]
    [InlineData("SymbolPackageFormat")]
    [InlineData("PackageLicenseExpression")]
    [InlineData("PackageReadmeFile")]
    [InlineData("PackageTags")]
    [InlineData("VersionPrefix")]
    [InlineData("VersionSuffix")]
    [InlineData("PackageProjectUrl")]
    [InlineData("RepositoryUrl")]
    [InlineData("RepositoryType")]
    [InlineData("PublishRepositoryUrl")]
    [InlineData("EnableSourceLink")]
    public void TheCoreDeclaresTheMetadataAPreviewNeeds(string property)
    {
        string? value = RepositoryLayout.PropertyOf(SharedProps, property);

        Assert.False(
            string.IsNullOrWhiteSpace(value),
            $"'{property}' is not declared in src/Directory.Build.props. " +
            "The package cannot be inspected, debugged or traced back to its source without it.");
    }

    [Fact]
    public void ThePackageIsTheOneTheMilestoneShips()
    {
        string? prefix = RepositoryLayout.PropertyOf(SharedProps, "VersionPrefix");
        string? suffix = RepositoryLayout.PropertyOf(SharedProps, "VersionSuffix");

        Assert.Equal("0.1.0", prefix);
        Assert.Equal("preview.1", suffix);
        Assert.True(
            File.Exists(RepositoryLayout.ProjectFileOf(RepositoryLayout.CorePackage)),
            "The package id falls out of the project file name, so the project file is the id.");
    }

    /// <summary>
    /// A description is what a consumer reads before restoring. The length floor is arbitrary but
    /// it is what stops the placeholder that says the package name back.
    /// </summary>
    public static TheoryData<string> ShippingPackages => [.. RepositoryLayout.Packages];

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void EveryPackageDescribesItself(string package)
    {
        string? description = RepositoryLayout.PropertyOf(
            RepositoryLayout.ProjectFileOf(package),
            "Description");

        Assert.NotNull(description);
        Assert.True(
            description!.Length > 40,
            $"'{package}' has a Description of {description.Length} characters. Say what it is for.");
    }

    [Fact]
    public void TheFilesThePackageCarriesExist()
    {
        foreach (string file in new[] { "README.md", "LICENSE" })
        {
            Assert.True(
                File.Exists(Path.Combine(RepositoryLayout.RepositoryRoot, file)),
                $"src/Directory.Build.props packs '{file}', but it is not in the repository root.");
        }
    }

    /// <summary>
    /// Only the shipping projects are published. Anything else becoming packable would be pushed by
    /// the same <c>dotnet pack</c> that pushes them, which is how a test helper ends up on NuGet.
    /// </summary>
    [Fact]
    public void OnlyShippingProjectsArePackable()
    {
        HashSet<string> shipping = [.. RepositoryLayout.Packages
            .Select(static package => Path.GetFullPath(RepositoryLayout.ProjectFileOf(package)))];

        List<string> offenders = RepositoryLayout.AllProjectFiles()
            .Where(path => !shipping.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
            .Where(static path => string.Equals(
                RepositoryLayout.PropertyOf(path, "IsPackable"), "true", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These projects declare IsPackable=true and are not the core: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// The core's consumer graph must be empty, and no package may flow a build-time-only analyzer
    /// to a consumer. The provider legitimately carries MSBuild and the core, so it is the shared
    /// props -- which every package inherits -- that this checks.
    /// </summary>
    [Fact]
    public void NoBuildTimeOnlyPackageReachesAConsumer()
    {
        Assert.Empty(RepositoryLayout.ProjectReferencesOf(RepositoryLayout.CorePackage));

        System.Xml.Linq.XDocument props = System.Xml.Linq.XDocument.Load(SharedProps);

        List<string> leaking = props.Descendants("PackageReference")
            .Where(static element => !string.Equals(
                element.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase))
            .Select(static element => element.Attribute("Include")?.Value ?? "(unnamed)")
            .ToList();

        Assert.True(
            leaking.Count == 0,
            "These package references would reach a consumer: " + string.Join(", ", leaking) + ".");
    }
}
