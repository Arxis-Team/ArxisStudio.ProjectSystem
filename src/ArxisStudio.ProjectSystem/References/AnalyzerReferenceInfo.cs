namespace ArxisStudio.ProjectSystem;

/// <summary>
/// An analyzer assembly a project loads at compile time.
/// </summary>
/// <remarks>
/// A path and nothing more. Roslyn's own <c>AnalyzerReference</c> is a class that loads the
/// assembly and instantiates what it finds; this one describes where the file is and does neither.
/// The <c>Info</c> suffix exists to keep the two apart in a consumer that has both in scope.
/// </remarks>
public sealed record AnalyzerReferenceInfo
{
    /// <summary>Gets the analyzer assembly's location.</summary>
    public required CanonicalPath AssemblyPath { get; init; }

    /// <summary>Gets the MSBuild condition guarding the reference, when it has one.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets any additional declared metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the analyzer assembly's file name.</summary>
    /// <returns>Something like <c>Analyzer: StyleCop.Analyzers.dll</c>.</returns>
    public override string ToString() => $"Analyzer: {AssemblyPath.FileName}";
}
