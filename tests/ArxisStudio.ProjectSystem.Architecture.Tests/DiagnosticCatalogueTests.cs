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
/// a code outside its range, or one nobody can look up is a defect in the contract itself.
/// </summary>
public sealed class DiagnosticCatalogueTests
{
    [Fact]
    public void EveryCode_IsUnique()
    {
        List<string> codes = [.. Codes().Select(static pair => pair.Code)];

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The core owns <c>APS1xxx</c>. The other ranges are reserved for packages that do not exist
    /// yet, and taking a number from one of them now would be spending somebody else's budget.
    /// </summary>
    [Fact]
    public void EveryCode_SitsInTheCoreRange()
    {
        List<string> offenders = [.. Codes()
            .Select(static pair => pair.Code)
            .Where(static code => !Regex.IsMatch(code, "^APS1[0-9]{3}$"))];

        Assert.True(
            offenders.Count == 0,
            "These codes are not in the core's APS1xxx range: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// A code a consumer cannot look up is a code they will guess at. The XML documentation ships
    /// in the package, so this checks the shipped file rather than the source.
    /// </summary>
    [Fact]
    public void EveryCode_IsDocumentedInTheShippedXml()
    {
        string documentation = File.ReadAllText(
            RepositoryLayout.DocumentationFileOf(RepositoryLayout.CorePackage));

        List<string> undocumented = [.. Codes()
            .Where(pair => !documentation.Contains(
                $"ProjectDiagnosticCodes.{pair.Name}", StringComparison.Ordinal))
            .Select(static pair => pair.Name)];

        Assert.True(
            undocumented.Count == 0,
            "These codes ship without documentation: " + string.Join(", ", undocumented) + ".");
    }

    /// <summary>
    /// The reserved ranges are a promise to the packages that will claim them, so they are written
    /// down where a reader of the catalogue will find them.
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

    /// <summary>
    /// Every code this milestone must be able to raise. The converse — that nothing is declared
    /// without a producer — is not mechanically checkable, and is a review rule instead.
    /// </summary>
    [Theory]
    [InlineData("UnsupportedEntryPoint")]
    [InlineData("ProviderFailed")]
    [InlineData("InvalidProviderResult")]
    public void TheCodesTheMilestoneNeeds_Exist(string name)
    {
        Assert.Contains(Codes(), pair => string.Equals(pair.Name, name, StringComparison.Ordinal));
    }

    private static IReadOnlyList<(string Name, string Code)> Codes() =>
        [.. RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage)
            .GetType($"{RepositoryLayout.CorePackage}.ProjectDiagnosticCodes")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (field.Name, (string)field.GetRawConstantValue()!))];
}
