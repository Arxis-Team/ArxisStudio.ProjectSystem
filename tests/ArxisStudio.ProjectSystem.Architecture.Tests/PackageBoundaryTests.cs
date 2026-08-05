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
    [Theory]
    [InlineData(RepositoryLayout.MSBuildPackage)]
    [InlineData(RepositoryLayout.NuGetPackage)]
    public void EveryPackage_DependsOnTheCore(string package)
    {
        Assert.Contains(
            RepositoryLayout.CorePackage,
            RepositoryLayout.ProjectReferencesOf(package),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// "Restore integration without duplicating project evaluation": the package manager restores
    /// through the core's operation boundary and reaches the MSBuild provider through nothing at
    /// all.
    /// </summary>
    /// <remarks>
    /// Checked in both directions and at both levels, because the failure it prevents is quiet. The
    /// moment the package manager could evaluate a project, something in it would — and then two
    /// packages would read project files, from two engines, and eventually disagree about one. The
    /// compiled check is the one that catches it arriving transitively.
    /// </remarks>
    [Fact]
    public void ThePackageManager_CannotReachTheProvider()
    {
        Assert.DoesNotContain(
            RepositoryLayout.MSBuildPackage,
            RepositoryLayout.ProjectReferencesOf(RepositoryLayout.NuGetPackage),
            StringComparer.Ordinal);

        Assert.DoesNotContain(
            RepositoryLayout.NuGetPackage,
            RepositoryLayout.ProjectReferencesOf(RepositoryLayout.MSBuildPackage),
            StringComparer.Ordinal);

        List<string> reached = RepositoryLayout.LoadAssembly(RepositoryLayout.NuGetPackage)
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name!)
            .Where(static name => name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
                || string.Equals(name, RepositoryLayout.MSBuildPackage, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            reached.Count == 0,
            "The package manager reaches an evaluation engine: " + string.Join(", ", reached) + ".");
    }

    /// <summary>
    /// "No reverse dependency from either core package." The adapter joins the two families, and
    /// nothing in either family may join it back.
    /// </summary>
    /// <remarks>
    /// The direction is the whole value of having an adapter at all. A core that could see it would
    /// make Markup a transitive dependency of anybody reading a snapshot, which is exactly the
    /// coupling the separate package exists to avoid.
    /// </remarks>
    [Theory]
    [InlineData(RepositoryLayout.CorePackage)]
    [InlineData(RepositoryLayout.MSBuildPackage)]
    [InlineData(RepositoryLayout.NuGetPackage)]
    public void NoPackage_DependsOnTheAdapter(string package)
    {
        Assert.DoesNotContain(
            RepositoryLayout.AdapterPackage,
            RepositoryLayout.ProjectReferencesOf(package),
            StringComparer.Ordinal);

        Assert.DoesNotContain(
            RepositoryLayout.AdapterPackage,
            RepositoryLayout.LoadAssembly(package).GetReferencedAssemblies().Select(static a => a.Name!),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The adapter adapts the model to Markup, and needs neither engine to do it. A consumer that
    /// wants XAML services should not acquire MSBuild and NuGet along the way.
    /// </summary>
    /// <remarks>
    /// The declarative half is not redundant with the compiled one and is the half that catches
    /// this. A C# assembly records only the references it actually used, so a project can declare a
    /// dependency on an engine, carry it into every consumer's output directory, and leave no trace
    /// in its own metadata until the day somebody writes the first line that touches it. Checking
    /// the project file is what notices on the day the reference is added.
    /// </remarks>
    [Fact]
    public void TheAdapter_ReachesNeitherEngine()
    {
        List<string> declared = RepositoryLayout.ProjectReferencesOf(RepositoryLayout.AdapterPackage)
            .Where(static name => string.Equals(name, RepositoryLayout.MSBuildPackage, StringComparison.Ordinal)
                || string.Equals(name, RepositoryLayout.NuGetPackage, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            declared.Count == 0,
            "The adapter declares a dependency on an engine it does not need: "
            + string.Join(", ", declared) + ".");

        List<string> reached = RepositoryLayout.LoadAssembly(RepositoryLayout.AdapterPackage)
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name!)
            .Where(static name => name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
                || name.StartsWith("NuGet.", StringComparison.Ordinal)
                || string.Equals(name, RepositoryLayout.MSBuildPackage, StringComparison.Ordinal)
                || string.Equals(name, RepositoryLayout.NuGetPackage, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            reached.Count == 0,
            "The adapter reaches an engine it does not need: " + string.Join(", ", reached) + ".");
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
