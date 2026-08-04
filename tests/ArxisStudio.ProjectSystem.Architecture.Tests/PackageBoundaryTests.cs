using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// The executable form of the specification's independence rules. If one of these fails, the
/// change belongs in a provider package rather than in the core — move the code, never relax
/// the test.
/// </summary>
public sealed class PackageBoundaryTests
{
    [Fact]
    public void Core_DeclaresNoForbiddenPackageReference()
    {
        List<string> offenders = RepositoryLayout.PackageReferencesOf(RepositoryLayout.CorePackage)
            .Where(ForbiddenDependencies.IsForbidden)
            .OrderBy(static id => id, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The core declares out-of-scope package references: " + string.Join(", ", offenders) + ". " +
            "The core is provider-neutral: MSBuild, NuGet, Roslyn, Avalonia, Markup and UI frameworks " +
            "belong in a provider or adapter package.");
    }

    /// <summary>
    /// The core stands alone this milestone. A project reference would be the first step towards
    /// a dependency the compiled check could only notice once it shipped something.
    /// </summary>
    [Fact]
    public void Core_DeclaresNoProjectReference()
    {
        IReadOnlySet<string> references = RepositoryLayout.ProjectReferencesOf(RepositoryLayout.CorePackage);

        Assert.True(
            references.Count == 0,
            "The core declares project references it should not have: " + string.Join(", ", references) + ".");
    }

    /// <summary>
    /// Reads the compiled assembly rather than the project file, which is what catches a
    /// forbidden dependency arriving transitively through something that looked harmless.
    /// </summary>
    [Fact]
    public void CompiledCore_ReferencesNoForbiddenAssembly()
    {
        List<string> offenders = RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .Where(ForbiddenDependencies.IsForbidden)
            .OrderBy(static name => name, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The compiled core references out-of-scope assemblies: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// Independence from the sibling libraries, stated positively: whatever else the core grows,
    /// it never reaches sideways into another ArxisStudio assembly.
    /// </summary>
    [Fact]
    public void CompiledCore_ReferencesNoOtherArxisStudioAssembly()
    {
        List<string> offenders = RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .Where(static name => name.StartsWith("ArxisStudio", System.StringComparison.Ordinal))
            .OrderBy(static name => name, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The compiled core references other ArxisStudio assemblies: " + string.Join(", ", offenders) + ". " +
            "Integration with Markup belongs in a separate adapter package that depends on both.");
    }
}
