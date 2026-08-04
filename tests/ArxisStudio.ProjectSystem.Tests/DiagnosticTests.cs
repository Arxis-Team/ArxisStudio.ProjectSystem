using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void Severity_OrdersFromInfoToError()
    {
        Assert.True(ProjectDiagnosticSeverity.Info < ProjectDiagnosticSeverity.Warning);
        Assert.True(ProjectDiagnosticSeverity.Warning < ProjectDiagnosticSeverity.Error);
        Assert.Equal(ProjectDiagnosticSeverity.Info, default);
    }

    [Fact]
    public void IsError_FollowsSeverity()
    {
        Assert.True(new ProjectDiagnostic("APS1001", "m", ProjectDiagnosticSeverity.Error).IsError);
        Assert.False(new ProjectDiagnostic("APS1001", "m", ProjectDiagnosticSeverity.Warning).IsError);
        Assert.False(new ProjectDiagnostic("APS1001", "m", ProjectDiagnosticSeverity.Info).IsError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCode_Throws(string? code)
    {
        Assert.Throws<ArgumentException>(
            () => new ProjectDiagnostic(code!, "message", ProjectDiagnosticSeverity.Error));
    }

    [Fact]
    public void ANullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ProjectDiagnostic("APS1001", null!, ProjectDiagnosticSeverity.Error));
    }

    [Fact]
    public void AbsentLocation_IsNoneRatherThanNull()
    {
        var diagnostic = new ProjectDiagnostic("APS1001", "message", ProjectDiagnosticSeverity.Error);

        Assert.True(diagnostic.FilePath.IsEmpty);
        Assert.True(diagnostic.Span.IsEmpty);
        Assert.True(diagnostic.Project.IsEmpty);
        Assert.Null(diagnostic.ProviderName);
    }

    [Fact]
    public void ForFile_CarriesThePathAndSpan()
    {
        CanonicalPath path = TestPaths.Project();

        ProjectDiagnostic diagnostic = ProjectDiagnostic.ForFile(
            "APS1001", "message", ProjectDiagnosticSeverity.Warning, path, FileSpan.At(12, 5));

        Assert.Equal(path, diagnostic.FilePath);
        Assert.Equal(12, diagnostic.Span.StartLine);
        Assert.Contains("(12,5)", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForProject_TakesItsLocationFromTheIdentity()
    {
        ProjectIdentity identity = ProjectIdentity.Create(WorkspaceIdentity.New(), TestPaths.Project());

        ProjectDiagnostic diagnostic = ProjectDiagnostic.ForProject(
            "APS1002", "message", ProjectDiagnosticSeverity.Error, identity);

        Assert.Equal(identity, diagnostic.Project);
        Assert.Equal(identity.ProjectFilePath, diagnostic.FilePath);
    }

    [Fact]
    public void WithProvider_AttributesACopy()
    {
        var diagnostic = new ProjectDiagnostic("APS1001", "message", ProjectDiagnosticSeverity.Error);
        ProjectDiagnostic attributed = diagnostic.WithProvider("MSBuild");

        Assert.Null(diagnostic.ProviderName);
        Assert.Equal("MSBuild", attributed.ProviderName);
    }

    [Fact]
    public void Equality_IsStructural()
    {
        var first = new ProjectDiagnostic("APS1001", "message", ProjectDiagnosticSeverity.Error);
        var second = new ProjectDiagnostic("APS1001", "message", ProjectDiagnosticSeverity.Error);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void ToString_LeadsWithSeverityAndCode()
    {
        Assert.StartsWith(
            "Error APS1001: ",
            new ProjectDiagnostic("APS1001", "message", ProjectDiagnosticSeverity.Error).ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Codes are a published contract, so a duplicate would mean two meanings behind one
    /// identifier and a consumer routing on it could not tell which it had.
    /// </summary>
    [Fact]
    public void EveryDeclaredCode_IsUniqueAndInTheCoreRange()
    {
        string[] codes = [.. Codes()];

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());

        foreach (string code in codes)
        {
            Assert.Matches("^APS1[0-9]{3}$", code);
        }
    }

    [Fact]
    public void EveryCodeTheMilestoneNeeds_Exists()
    {
        Assert.Equal("APS1001", ProjectDiagnosticCodes.UnsupportedEntryPoint);
        Assert.Equal("APS1002", ProjectDiagnosticCodes.ProviderFailed);
        Assert.Equal("APS1003", ProjectDiagnosticCodes.InvalidProviderResult);
    }

    [Fact]
    public void FileSpan_DefaultIsNone()
    {
        Assert.True(FileSpan.None.IsEmpty);
        Assert.Equal(default, FileSpan.None);
        Assert.Equal("(none)", FileSpan.None.ToString());
    }

    [Fact]
    public void FileSpan_At_NamesOnePosition()
    {
        FileSpan span = FileSpan.At(3, 7);

        Assert.Equal(3, span.StartLine);
        Assert.Equal(7, span.StartColumn);
        Assert.Equal(3, span.EndLine);
        Assert.Equal(7, span.EndColumn);
        Assert.Equal("(3,7)", span.ToString());
    }

    [Fact]
    public void FileSpan_Create_SpansARange()
    {
        FileSpan span = FileSpan.Create(3, 7, 5, 9);

        Assert.Equal("(3,7)-(5,9)", span.ToString());
        Assert.False(span.IsEmpty);
    }

    [Fact]
    public void FileSpan_ThatEndsBeforeItStarts_Throws()
    {
        Assert.Throws<ArgumentException>(() => FileSpan.Create(5, 1, 3, 1));
        Assert.Throws<ArgumentException>(() => FileSpan.Create(5, 9, 5, 2));
    }

    [Fact]
    public void FileSpan_WithANegativePosition_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileSpan.At(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileSpan.At(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileSpan.Create(-1, 0, 0, 0));
    }

    private static System.Collections.Generic.IEnumerable<string> Codes() =>
        typeof(ProjectDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!);
}
