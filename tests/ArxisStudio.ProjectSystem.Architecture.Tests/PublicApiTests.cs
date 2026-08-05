using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// The public surface is tracked in a file a reviewer reads, and this checks the tracking rather
/// than trusting it.
/// </summary>
public sealed class PublicApiTests
{
    public static TheoryData<string> ShippingPackages => [.. RepositoryLayout.Packages];

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void EveryPackage_TracksItsPublicSurface(string package)
    {
        Assert.True(File.Exists(ApiFile(package, "Shipped")), $"'{ApiFile(package, "Shipped")}' is missing.");
        Assert.True(File.Exists(ApiFile(package, "Unshipped")), $"'{ApiFile(package, "Unshipped")}' is missing.");
    }

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void EveryPackage_ReferencesTheAnalyzerThatEnforcesIt(string package)
    {
        Assert.Contains(
            "Microsoft.CodeAnalysis.PublicApiAnalyzers",
            RepositoryLayout.PackageReferencesOf(package),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Declaring the analyzer is not the same as failing the build on an undeclared member, and only
    /// the second one is worth anything.
    /// </summary>
    [Theory]
    [InlineData("RS0016")]
    [InlineData("RS0017")]
    public void TheAnalyzersDiagnostics_AreErrors(string rule)
    {
        string editorConfig = File.ReadAllText(Path.Combine(RepositoryLayout.RepositoryRoot, ".editorconfig"));

        Assert.Contains($"dotnet_diagnostic.{rule}.severity = error", editorConfig, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing has shipped: everything stays unshipped until a release is deliberately prepared,
    /// because moving an entry into the shipped file is a promise about compatibility.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void NothingHasShippedYet(string package)
    {
        Assert.True(
            !Declared(ApiFile(package, "Shipped")).Any(),
            $"'{package}' has entries in PublicAPI.Shipped.txt. Moving entries into it is a separate, " +
            "deliberate act that belongs to release preparation.");
    }

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void EveryPublicType_IsDeclared(string package)
    {
        HashSet<string> declared =
            [.. Declared(ApiFile(package, "Shipped")).Concat(Declared(ApiFile(package, "Unshipped")))];

        List<string> missing = RepositoryLayout.LoadAssembly(package)
            .GetExportedTypes()
            .Select(static type => type.FullName!.Replace('+', '.'))
            .Where(name => !declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These public types of '{package}' are not declared: " + string.Join(", ", missing) + ".");
    }

    [Theory]
    [MemberData(nameof(ShippingPackages))]
    public void EveryDeclaredType_StillExists(string package)
    {
        HashSet<string> live = [.. RepositoryLayout.LoadAssembly(package)
            .GetExportedTypes()
            .Select(static type => type.FullName!.Replace('+', '.'))];

        // A bare line with no parenthesis and no arrow is a type; anything else is a member.
        List<string> stale = Declared(ApiFile(package, "Shipped"))
            .Concat(Declared(ApiFile(package, "Unshipped")))
            .Where(static line => !line.Contains('(', StringComparison.Ordinal)
                && !line.Contains("->", StringComparison.Ordinal))
            .Where(name => !live.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"These types declared by '{package}' no longer exist: " + string.Join(", ", stale) + ".");
    }

    private static string ApiFile(string package, string which) =>
        Path.Combine(RepositoryLayout.RepositoryRoot, "src", package, $"PublicAPI.{which}.txt");

    private static IEnumerable<string> Declared(string path) =>
        !File.Exists(path)
            ? []
            : File.ReadAllLines(path)
                .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
}
