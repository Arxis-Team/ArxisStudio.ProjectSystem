using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using ArxisStudio.Markup;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Translates diagnostics between the two families, so a tool can show one list.
/// </summary>
/// <remarks>
/// <para>
/// A designer produces both kinds at once — the project would not restore, and the document names a
/// control that does not exist — and a user does not care which library noticed. Both sides already
/// agree on the shape of a diagnostic: a stable code, a message for people, a severity, a location.
/// The severities even line up value for value.
/// </para>
/// <para>
/// <b>The positions do not,</b> and that is the whole difficulty. Markup measures a span as an
/// offset and a length into the document text; the project model measures it as lines and columns,
/// because that is what a compiler and MSBuild report. Neither can be turned into the other without
/// the text, so the text is asked for: hand it over and the position survives, leave it out and the
/// diagnostic arrives without one rather than with a wrong one.
/// </para>
/// </remarks>
public static class ProjectMarkupDiagnostics
{
    /// <summary>The provider name a translated Markup diagnostic carries.</summary>
    /// <remarks>
    /// So that a tool showing one list can still say which library noticed, and route accordingly:
    /// a XAML problem is fixed in the document, a project problem is not.
    /// </remarks>
    public const string MarkupProviderName = "Markup";

    /// <summary>
    /// Turns a Markup diagnostic into a project one.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to translate.</param>
    /// <param name="documentText">
    /// The text the diagnostic's span points into, or <see langword="null"/> to translate without a
    /// position. Supplying it is what turns an offset into a line and column.
    /// </param>
    /// <param name="filePath">
    /// The file to attribute it to, or <see cref="CanonicalPath.None"/> to take one from the
    /// diagnostic's own document URI when that URI names a file.
    /// </param>
    /// <returns>The translated diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public static ProjectDiagnostic ToProject(
        MarkupDiagnostic diagnostic,
        string? documentText = null,
        CanonicalPath filePath = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new ProjectDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity switch
            {
                MarkupDiagnosticSeverity.Error => ProjectDiagnosticSeverity.Error,
                MarkupDiagnosticSeverity.Warning => ProjectDiagnosticSeverity.Warning,
                _ => ProjectDiagnosticSeverity.Info,
            })
        {
            FilePath = filePath.IsEmpty ? FileOf(diagnostic.DocumentUri) : filePath,
            Span = diagnostic.Span is { } span && documentText is not null
                ? ToFileSpan(span, documentText)
                : FileSpan.None,
            ProviderName = MarkupProviderName,
        };
    }

    /// <summary>Translates a batch, which is how they usually arrive.</summary>
    /// <param name="diagnostics">The diagnostics to translate.</param>
    /// <param name="documentText">The text their spans point into, or <see langword="null"/>.</param>
    /// <param name="filePath">The file to attribute them to, or <see cref="CanonicalPath.None"/>.</param>
    /// <returns>The translated diagnostics, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<ProjectDiagnostic> ToProject(
        IEnumerable<MarkupDiagnostic> diagnostics,
        string? documentText = null,
        CanonicalPath filePath = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        ImmutableArray<ProjectDiagnostic>.Builder translated =
            ImmutableArray.CreateBuilder<ProjectDiagnostic>();

        foreach (MarkupDiagnostic diagnostic in diagnostics)
        {
            translated.Add(ToProject(diagnostic, documentText, filePath));
        }

        return translated.ToImmutable();
    }

    /// <summary>
    /// Turns a project diagnostic into a Markup one, so a document view can show why the project it
    /// belongs to will not build.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to translate.</param>
    /// <param name="documentText">
    /// The text of the file it points at, or <see langword="null"/> to translate without a position.
    /// </param>
    /// <returns>The translated diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public static MarkupDiagnostic ToMarkup(ProjectDiagnostic diagnostic, string? documentText = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new MarkupDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity switch
            {
                ProjectDiagnosticSeverity.Error => MarkupDiagnosticSeverity.Error,
                ProjectDiagnosticSeverity.Warning => MarkupDiagnosticSeverity.Warning,
                _ => MarkupDiagnosticSeverity.Info,
            },
            diagnostic.FilePath.IsEmpty ? null : new Uri(diagnostic.FilePath.Value),
            documentText is null || diagnostic.Span.IsEmpty
                ? null
                : ToTextSpan(diagnostic.Span, documentText));
    }

    /// <summary>The file a document URI names, when it names one.</summary>
    /// <remarks>
    /// An <c>avares</c> URI does not: it addresses a resource inside an assembly, and the file it
    /// came from is a question only a project model can answer. Attributing the diagnostic to
    /// nothing is better than attributing it to a path invented here.
    /// </remarks>
    private static CanonicalPath FileOf(Uri? documentUri) =>
        documentUri is { IsAbsoluteUri: true, IsFile: true }
            && CanonicalPath.TryCreate(documentUri.LocalPath, out CanonicalPath path)
                ? path
                : CanonicalPath.None;

    /// <summary>
    /// Converts an offset span into lines and columns by counting the line breaks before it.
    /// </summary>
    /// <remarks>
    /// Both are one-based, which is what every compiler, every editor and MSBuild report. A line
    /// break is <c>\n</c>, and a preceding <c>\r</c> belongs to it — counting <c>\r\n</c> as two
    /// would put every line of a Windows file one further down than it is.
    /// </remarks>
    private static FileSpan ToFileSpan(TextSpan span, string text)
    {
        (int startLine, int startColumn) = PositionOf(span.Start, text);
        (int endLine, int endColumn) = PositionOf(span.End, text);

        return FileSpan.Create(startLine, startColumn, endLine, endColumn);
    }

    private static (int Line, int Column) PositionOf(int offset, string text)
    {
        int line = 1;
        int lineStart = 0;
        int limit = Math.Min(offset, text.Length);

        for (int index = 0; index < limit; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return (line, limit - lineStart + 1);
    }

    /// <summary>Converts lines and columns back into an offset span.</summary>
    private static TextSpan ToTextSpan(FileSpan span, string text)
    {
        int start = OffsetOf(span.StartLine, span.StartColumn, text);
        int end = span.EndLine > 0 ? OffsetOf(span.EndLine, span.EndColumn, text) : start;

        return new TextSpan(start, Math.Max(0, end - start));
    }

    private static int OffsetOf(int line, int column, string text)
    {
        int current = 1;
        int index = 0;

        while (current < line && index < text.Length)
        {
            if (text[index] == '\n')
            {
                current++;
            }

            index++;
        }

        // A column of zero means "this line", which MSBuild reports for a diagnostic it can place
        // no more precisely than that.
        int offset = index + Math.Max(0, column - 1);

        return Math.Min(offset, text.Length);
    }
}
