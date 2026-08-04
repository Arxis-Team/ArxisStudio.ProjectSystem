using System;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A structured report of something the project system noticed, in place of an exception.
/// </summary>
/// <remarks>
/// <para>
/// A missing project, an unsupported entry point, an unresolved reference, a failed evaluation —
/// these are ordinary tool scenarios, not exceptional ones, and a tool has to be able to show all
/// of them at once rather than stop at the first. So they are values, and a load that hits several
/// returns all of them.
/// </para>
/// <para>
/// Decide what a diagnostic means by looking at <see cref="Code"/> and <see cref="Severity"/>,
/// never by reading <see cref="Message"/>: the message is for people and may be reworded, while
/// the code is a stable contract that consumers suppress and route on.
/// </para>
/// <para>
/// Exceptions remain reserved for invalid use of this library's own API, cancellation, disposed
/// objects, broken invariants, and failures with no structured result to return. A provider that
/// throws is caught and reported as <see cref="ProjectDiagnosticCodes.ProviderFailed"/>; the
/// exception itself never becomes part of the public contract.
/// </para>
/// </remarks>
/// <param name="Code">
/// A stable, machine-readable identifier. Each layer owns a numeric range; codes are never reused
/// for a different meaning. See <see cref="ProjectDiagnosticCodes"/>.
/// </param>
/// <param name="Message">A human-readable explanation. Not a stable contract.</param>
/// <param name="Severity">How seriously to take it.</param>
public sealed record ProjectDiagnostic(string Code, string Message, ProjectDiagnosticSeverity Severity)
{
    /// <summary>Gets the stable, machine-readable identifier.</summary>
    public string Code { get; init; } = string.IsNullOrWhiteSpace(Code)
        ? throw new ArgumentException("A diagnostic code cannot be null or blank.", nameof(Code))
        : Code;

    /// <summary>Gets the human-readable explanation.</summary>
    public string Message { get; init; } = Message ?? throw new ArgumentNullException(nameof(Message));

    /// <summary>Gets the file concerned, or <see cref="CanonicalPath.None"/> when there is none.</summary>
    public CanonicalPath FilePath { get; init; }

    /// <summary>Gets the range concerned, or <see cref="FileSpan.None"/> when the diagnostic is no more precise than the file.</summary>
    public FileSpan Span { get; init; }

    /// <summary>Gets the project concerned, or <see cref="ProjectIdentity.None"/> when the diagnostic is not about one project.</summary>
    public ProjectIdentity Project { get; init; }

    /// <summary>Gets the name of the provider that raised this, when a provider did.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Gets a value indicating whether this diagnostic prevents a correct result.</summary>
    public bool IsError => Severity == ProjectDiagnosticSeverity.Error;

    /// <summary>Creates a diagnostic about a file, optionally at a position in it.</summary>
    /// <param name="code">The stable identifier.</param>
    /// <param name="message">The human-readable explanation.</param>
    /// <param name="severity">How seriously to take it.</param>
    /// <param name="filePath">The file concerned.</param>
    /// <param name="span">The range concerned.</param>
    /// <returns>The diagnostic.</returns>
    public static ProjectDiagnostic ForFile(
        string code,
        string message,
        ProjectDiagnosticSeverity severity,
        CanonicalPath filePath,
        FileSpan span = default) =>
        new(code, message, severity) { FilePath = filePath, Span = span };

    /// <summary>Creates a diagnostic about one project.</summary>
    /// <param name="code">The stable identifier.</param>
    /// <param name="message">The human-readable explanation.</param>
    /// <param name="severity">How seriously to take it.</param>
    /// <param name="project">The project concerned.</param>
    /// <returns>The diagnostic, carrying the project's file path as its location.</returns>
    public static ProjectDiagnostic ForProject(
        string code,
        string message,
        ProjectDiagnosticSeverity severity,
        ProjectIdentity project) =>
        new(code, message, severity) { Project = project, FilePath = project.ProjectFilePath };

    /// <summary>Returns a copy attributed to a provider.</summary>
    /// <param name="providerName">The provider's stable name.</param>
    /// <returns>The copy.</returns>
    public ProjectDiagnostic WithProvider(string? providerName) => this with { ProviderName = providerName };

    /// <summary>Returns a string of the form <c>severity CODE: message</c> with the location, if any.</summary>
    /// <returns>A readable representation of the diagnostic.</returns>
    public override string ToString()
    {
        string location = FilePath.IsEmpty
            ? string.Empty
            : Span.IsEmpty ? $" ({FilePath})" : $" ({FilePath}{Span})";

        return $"{Severity} {Code}: {Message}{location}";
    }
}
