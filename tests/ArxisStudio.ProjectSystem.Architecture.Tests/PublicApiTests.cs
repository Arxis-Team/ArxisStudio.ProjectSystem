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
    private static string Shipped =>
        Path.Combine(RepositoryLayout.RepositoryRoot, "src", RepositoryLayout.CorePackage, "PublicAPI.Shipped.txt");

    private static string Unshipped =>
        Path.Combine(RepositoryLayout.RepositoryRoot, "src", RepositoryLayout.CorePackage, "PublicAPI.Unshipped.txt");

    [Fact]
    public void TheCore_TracksItsPublicSurface()
    {
        Assert.True(File.Exists(Shipped), $"'{Shipped}' is missing.");
        Assert.True(File.Exists(Unshipped), $"'{Unshipped}' is missing.");
    }

    [Fact]
    public void TheCore_ReferencesTheAnalyzerThatEnforcesIt()
    {
        Assert.Contains(
            "Microsoft.CodeAnalysis.PublicApiAnalyzers",
            RepositoryLayout.PackageReferencesOf(RepositoryLayout.CorePackage),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Declaring the analyzer is not the same as failing the build on an undeclared member, and
    /// only the second one is worth anything.
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
    /// The milestone ships nothing: everything stays unshipped until a release is deliberately
    /// prepared, because moving an entry into the shipped file is a promise about compatibility.
    /// </summary>
    [Fact]
    public void Milestone0_HasShippedNothingYet()
    {
        IEnumerable<string> entries = Declared(Shipped);

        Assert.True(
            !entries.Any(),
            "PublicAPI.Shipped.txt is not empty. Moving entries into it is a separate, deliberate act " +
            "that belongs to release preparation.");
    }

    [Fact]
    public void EveryPublicType_IsDeclared()
    {
        HashSet<string> declared = [.. Declared(Shipped).Concat(Declared(Unshipped))];

        List<string> missing = RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetExportedTypes()
            .Select(static type => type.FullName!.Replace('+', '.'))
            .Where(name => !declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These public types are not declared in the API files: " + string.Join(", ", missing) + ".");
    }

    [Fact]
    public void EveryDeclaredType_StillExists()
    {
        HashSet<string> live = [.. RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetExportedTypes()
            .Select(static type => type.FullName!.Replace('+', '.'))];

        // A bare line with no parenthesis and no arrow is a type; anything else is a member.
        List<string> stale = Declared(Shipped)
            .Concat(Declared(Unshipped))
            .Where(static line => !line.Contains('(', StringComparison.Ordinal)
                && !line.Contains("->", StringComparison.Ordinal))
            .Where(name => !live.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These declared types no longer exist: " + string.Join(", ", stale) + ".");
    }

    private static IEnumerable<string> Declared(string path) =>
        !File.Exists(path)
            ? []
            : File.ReadAllLines(path)
                .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
}
