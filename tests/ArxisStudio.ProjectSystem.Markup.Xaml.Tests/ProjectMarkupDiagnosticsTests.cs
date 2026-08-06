using System;
using System.Collections.Immutable;
using System.IO;
using ArxisStudio.Markup;
using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// Carrying a diagnostic across the boundary between the two families.
/// </summary>
/// <remarks>
/// The severities line up value for value; the positions do not, and most of what is worth asserting
/// here is about that.
/// </remarks>
public sealed class ProjectMarkupDiagnosticsTests
{
    private const string Document = "<UserControl>\n  <Button />\n</UserControl>";

    private static CanonicalPath File => CanonicalPath.Create(
        Path.Combine(Path.GetTempPath(), "arxis-diag", "Main.axaml"));

    [Theory]
    [InlineData(MarkupDiagnosticSeverity.Error, ProjectDiagnosticSeverity.Error)]
    [InlineData(MarkupDiagnosticSeverity.Warning, ProjectDiagnosticSeverity.Warning)]
    [InlineData(MarkupDiagnosticSeverity.Info, ProjectDiagnosticSeverity.Info)]
    public void Severities_SurviveBothWays(
        MarkupDiagnosticSeverity markup, ProjectDiagnosticSeverity project)
    {
        Assert.Equal(
            project,
            ProjectMarkupDiagnostics.ToProject(new MarkupDiagnostic("AXAML001", "x", markup)).Severity);

        Assert.Equal(
            markup,
            ProjectMarkupDiagnostics.ToMarkup(new ProjectDiagnostic("APS1001", "x", project)).Severity);
    }

    [Fact]
    public void ACodeAndMessage_AreCarriedUnchanged()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic("AXAML042", "Button is not a thing", MarkupDiagnosticSeverity.Error));

        Assert.Equal("AXAML042", translated.Code);
        Assert.Equal("Button is not a thing", translated.Message);

        // So a tool showing one list can still say which library noticed, and route accordingly.
        Assert.Equal("Markup", translated.ProviderName);
    }

    /// <summary>
    /// Markup counts characters and the project model counts lines, so the text is what makes the
    /// two commensurable.
    /// </summary>
    [Fact]
    public void WithTheText_AnOffsetBecomesALineAndColumn()
    {
        // The '<' of <Button>, on the second line, at the third column.
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic("AXAML001", "x", MarkupDiagnosticSeverity.Error, null, new TextSpan(16, 9)),
            Document);

        Assert.Equal(2, translated.Span.StartLine);
        Assert.Equal(3, translated.Span.StartColumn);
        Assert.Equal(2, translated.Span.EndLine);
        Assert.Equal(12, translated.Span.EndColumn);
    }

    /// <summary>
    /// Without it, no position at all rather than a wrong one. A diagnostic pointing at the wrong
    /// line is worse than one pointing nowhere, because somebody will act on it.
    /// </summary>
    [Fact]
    public void WithoutTheText_ThereIsNoPositionRatherThanAWrongOne()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic("AXAML001", "x", MarkupDiagnosticSeverity.Error, null, new TextSpan(16, 9)));

        Assert.True(translated.Span.IsEmpty);
    }

    /// <summary>
    /// Counting <c>\r\n</c> as two breaks would put every line of a Windows file one further down
    /// than it is.
    /// </summary>
    [Fact]
    public void WindowsLineBreaks_CountOnce()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic("AXAML001", "x", MarkupDiagnosticSeverity.Error, null, new TextSpan(17, 1)),
            "<UserControl>\r\n  <Button />\r\n</UserControl>");

        Assert.Equal(2, translated.Span.StartLine);
    }

    [Fact]
    public void APositionSurvivesARoundTrip()
    {
        var original = new MarkupDiagnostic(
            "AXAML001", "x", MarkupDiagnosticSeverity.Error, new Uri(File.Value), new TextSpan(16, 9));

        MarkupDiagnostic returned = ProjectMarkupDiagnostics.ToMarkup(
            ProjectMarkupDiagnostics.ToProject(original, Document), Document);

        Assert.Equal(original.Span, returned.Span);
        Assert.Equal(original.Code, returned.Code);
        Assert.Equal(original.Severity, returned.Severity);
    }

    [Fact]
    public void AFileDocumentUri_BecomesAPath()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic("AXAML001", "x", MarkupDiagnosticSeverity.Error, new Uri(File.Value)));

        Assert.Equal(File, translated.FilePath);
    }

    /// <summary>
    /// An avares URI addresses a resource inside an assembly. Which file it came from is a question
    /// only a project model can answer, and inventing a path here would answer it wrongly.
    /// </summary>
    [Fact]
    public void AnAvaresDocumentUri_BecomesNoPath()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic(
                "AXAML001", "x", MarkupDiagnosticSeverity.Error, new Uri("avares://MyApp/Main.axaml")));

        Assert.True(translated.FilePath.IsEmpty);
    }

    [Fact]
    public void AnExplicitPath_WinsOverTheDocumentUri()
    {
        ProjectDiagnostic translated = ProjectMarkupDiagnostics.ToProject(
            new MarkupDiagnostic(
                "AXAML001", "x", MarkupDiagnosticSeverity.Error, new Uri("avares://MyApp/Main.axaml")),
            documentText: null,
            filePath: File);

        Assert.Equal(File, translated.FilePath);
    }

    [Fact]
    public void ABatch_KeepsItsOrder()
    {
        ImmutableArray<ProjectDiagnostic> translated = ProjectMarkupDiagnostics.ToProject(
            [
                new MarkupDiagnostic("A", "first", MarkupDiagnosticSeverity.Error),
                new MarkupDiagnostic("B", "second", MarkupDiagnosticSeverity.Warning),
            ]);

        Assert.Equal(["A", "B"], System.Linq.Enumerable.Select(translated, static d => d.Code));
    }

    [Fact]
    public void AProjectDiagnosticWithNoLocation_TranslatesWithNone()
    {
        MarkupDiagnostic translated = ProjectMarkupDiagnostics.ToMarkup(
            new ProjectDiagnostic("APS2001", "no MSBuild", ProjectDiagnosticSeverity.Error));

        Assert.Null(translated.DocumentUri);
        Assert.Null(translated.Span);
    }

    /// <summary>
    /// MSBuild reports a column of zero for a diagnostic it can place no more precisely than a
    /// line, and the offset for that is the start of the line rather than one character into it.
    /// </summary>
    [Fact]
    public void AColumnOfZero_IsTheStartOfTheLine()
    {
        MarkupDiagnostic translated = ProjectMarkupDiagnostics.ToMarkup(
            new ProjectDiagnostic("APS2002", "x", ProjectDiagnosticSeverity.Error)
            {
                Span = FileSpan.At(2),
            },
            Document);

        Assert.Equal(14, translated.Span!.Value.Start);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectMarkupDiagnostics.ToProject((MarkupDiagnostic)null!));
        Assert.Throws<ArgumentNullException>(() =>
            ProjectMarkupDiagnostics.ToProject((System.Collections.Generic.IEnumerable<MarkupDiagnostic>)null!));
        Assert.Throws<ArgumentNullException>(() => ProjectMarkupDiagnostics.ToMarkup(null!));
    }
}
