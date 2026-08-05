using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// The executable form of the specification's independence rules. If one of these fails, the change
/// belongs in a different package — move the code, never relax the test.
/// </summary>
public sealed class PackageBoundaryTests
{
    public static TheoryData<string> ShippingPackages => [.. RepositoryLayout.Packages];

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void NoPackage_DeclaresAForbiddenPackageReference(string package)
    {
        List<string> offenders = RepositoryLayout.PackageReferencesOf(package)
            .Where(id => ForbiddenDependencies.IsForbiddenReference(package, id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"'{package}' declares out-of-scope package references: " + string.Join(", ", offenders) + ".");
    }

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void NoCompiledPackage_ReferencesAForbiddenAssembly(string package)
    {
        List<string> offenders = RepositoryLayout.LoadAssembly(package)
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .Where(id => ForbiddenDependencies.IsForbiddenReference(package, id))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The compiled '{package}' references out-of-scope assemblies: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// The core stands alone. A project reference would be the first step towards a dependency the
    /// compiled check could only notice once it shipped something.
    /// </summary>
    [Fact]
    public void TheCore_DeclaresNoProjectReference()
    {
        IReadOnlySet<string> references = RepositoryLayout.ProjectReferencesOf(RepositoryLayout.CorePackage);

        Assert.True(
            references.Count == 0,
            "The core declares project references it should not have: " + string.Join(", ", references) + ".");
    }

    /// <summary>
    /// Independence from the sibling libraries, stated positively: whatever else the core grows, it
    /// never reaches sideways into another ArxisStudio assembly.
    /// </summary>
    [Fact]
    public void TheCompiledCore_ReferencesNoOtherArxisStudioAssembly()
    {
        List<string> offenders = RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .Where(static name => name.StartsWith("ArxisStudio", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The compiled core references other ArxisStudio assemblies: " + string.Join(", ", offenders) + ". " +
            "Integration with Markup belongs in a separate adapter package that depends on both.");
    }

    /// <summary>The dependency runs one way, and this is the direction.</summary>
    [Fact]
    public void TheProvider_DependsOnTheCore()
    {
        Assert.Contains(
            RepositoryLayout.CorePackage,
            RepositoryLayout.ProjectReferencesOf(RepositoryLayout.MSBuildPackage),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// MSBuild's assemblies must come from the SDK the locator finds, not from these packages.
    /// Shipping both copies produces a process holding two different MSBuilds, and it fails later
    /// and somewhere unrelated.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.Build")]
    [InlineData("Microsoft.Build.Framework")]
    public void TheProvider_TakesNoRuntimeAssetsFromMSBuild(string package)
    {
        System.Xml.Linq.XDocument project = System.Xml.Linq.XDocument.Load(
            RepositoryLayout.ProjectFileOf(RepositoryLayout.MSBuildPackage));

        System.Xml.Linq.XElement? reference = project.Descendants("PackageReference")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("Include")?.Value, package, StringComparison.Ordinal));

        Assert.NotNull(reference);
        Assert.Equal("runtime", reference.Attribute("ExcludeAssets")?.Value);
    }
}
