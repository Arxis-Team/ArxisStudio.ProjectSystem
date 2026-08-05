using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// Diagnostic codes are a published contract: consumers suppress and route on them, so a duplicate,
/// a code outside its package's range, or one nobody can look up is a defect in the contract itself.
/// </summary>
/// <remarks>
/// Every catalogue owns a numeric range, and the ranges are divided by concern rather than by
/// assembly — one package may hold two catalogues, as the MSBuild provider does for evaluating a
/// project and for building one. All of them are listed here, so a catalogue that appears without a
/// range, or takes a number from somebody else's, fails rather than ships.
/// </remarks>
public sealed class DiagnosticCatalogueTests
{
    /// <summary>Package, the type holding a set of codes, and the range that set owns.</summary>
    private static readonly (string Package, string Catalogue, string Range)[] All =
    [
        (RepositoryLayout.CorePackage, "ProjectDiagnosticCodes", "^APS1[0-9]{3}$"),
        (RepositoryLayout.MSBuildPackage, "MSBuildDiagnosticCodes", "^APS2[0-9]{3}$"),
        (RepositoryLayout.MSBuildPackage, "OperationDiagnosticCodes", "^APS3[0-9]{3}$"),
        (RepositoryLayout.NuGetPackage, "PackageDiagnosticCodes", "^APS4[0-9]{3}$"),
    ];

    public static TheoryData<string, string, string> Catalogues
    {
        get
        {
            TheoryData<string, string, string> data = [];

            foreach ((string package, string catalogue, string range) in All)
            {
                data.Add(package, catalogue, range);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void EveryCode_IsUnique(string package, string catalogue, string range)
    {
        _ = range;

        List<string> codes = [.. Codes(package, catalogue).Select(static pair => pair.Code)];

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The other ranges belong to packages that may not exist yet, and taking a number from one of
    /// them now would be spending somebody else's budget.
    /// </summary>
    [Theory]
    [MemberData(nameof(Catalogues))]
    public void EveryCode_SitsInItsPackagesRange(string package, string catalogue, string range)
    {
        List<string> offenders = [.. Codes(package, catalogue)
            .Select(static pair => pair.Code)
            .Where(code => !Regex.IsMatch(code, range))];

        Assert.True(
            offenders.Count == 0,
            $"These codes of '{package}' are outside {range}: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// No code may be shared between catalogues, whatever range it claims to be in.
    /// </summary>
    [Fact]
    public void NoCode_IsClaimedTwice()
    {
        List<string> all = [.. All
            .SelectMany(static row => Codes(row.Package, row.Catalogue))
            .Select(static pair => pair.Code)];

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A code a consumer cannot look up is a code they will guess at. The XML documentation ships in
    /// the package, so this checks the shipped file rather than the source.
    /// </summary>
    [Theory]
    [MemberData(nameof(Catalogues))]
    public void EveryCode_IsDocumentedInTheShippedXml(string package, string catalogue, string range)
    {
        _ = range;

        string documentation = File.ReadAllText(RepositoryLayout.DocumentationFileOf(package));

        List<string> undocumented = [.. Codes(package, catalogue)
            .Where(pair => !documentation.Contains($"{catalogue}.{pair.Name}", StringComparison.Ordinal))
            .Select(static pair => pair.Name)];

        Assert.True(
            undocumented.Count == 0,
            $"These codes of '{package}' ship without documentation: " + string.Join(", ", undocumented) + ".");
    }

    /// <summary>
    /// The reserved ranges are a promise to the packages that will claim them, so they are written
    /// down where a reader of the core's catalogue will find them.
    /// </summary>
    [Theory]
    [InlineData("APS1xxx")]
    [InlineData("APS2xxx")]
    [InlineData("APS3xxx")]
    [InlineData("APS4xxx")]
    [InlineData("APS5xxx")]
    public void TheReservedRanges_AreRecorded(string range)
    {
        string documentation = File.ReadAllText(
            RepositoryLayout.DocumentationFileOf(RepositoryLayout.CorePackage));

        Assert.Contains(range, documentation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepositoryLayout.CorePackage, "ProjectDiagnosticCodes", "UnsupportedEntryPoint")]
    [InlineData(RepositoryLayout.CorePackage, "ProjectDiagnosticCodes", "ProviderFailed")]
    [InlineData(RepositoryLayout.CorePackage, "ProjectDiagnosticCodes", "InvalidProviderResult")]
    [InlineData(RepositoryLayout.MSBuildPackage, "MSBuildDiagnosticCodes", "MSBuildNotFound")]
    [InlineData(RepositoryLayout.MSBuildPackage, "MSBuildDiagnosticCodes", "EvaluationFailed")]
    [InlineData(RepositoryLayout.MSBuildPackage, "MSBuildDiagnosticCodes", "ProjectFileNotFound")]
    [InlineData(RepositoryLayout.CorePackage, "ProjectDiagnosticCodes", "UnsupportedOperation")]
    [InlineData(RepositoryLayout.MSBuildPackage, "OperationDiagnosticCodes", "OperationFailed")]
    public void TheCodesAMilestoneNeeds_Exist(string package, string catalogue, string name)
    {
        Assert.Contains(Codes(package, catalogue), pair => string.Equals(pair.Name, name, StringComparison.Ordinal));
    }

    private static IReadOnlyList<(string Name, string Code)> Codes(string package, string catalogue) =>
        [.. RepositoryLayout.LoadAssembly(package)
            .GetType($"{package}.{catalogue}")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (field.Name, (string)field.GetRawConstantValue()!))];
}
