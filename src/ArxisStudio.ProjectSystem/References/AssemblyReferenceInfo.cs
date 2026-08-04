using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A direct reference to an assembly, by name or by file.
/// </summary>
/// <remarks>
/// <see cref="HintPath"/> is what the project file suggests; <see cref="ResolvedPath"/> is where a
/// provider found it. Either may be <see cref="CanonicalPath.None"/> — a reference to a framework
/// assembly by bare name has no hint path, and an unresolved reference has no resolved one. The
/// pair is what lets a consumer tell "not resolved yet" from "resolved to somewhere unexpected".
/// </remarks>
public sealed record AssemblyReferenceInfo
{
    /// <summary>Gets the assembly name as declared.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the path the project file suggests, when it suggests one.</summary>
    public CanonicalPath HintPath { get; init; }

    /// <summary>Gets the path a provider resolved the reference to, when it resolved one.</summary>
    public CanonicalPath ResolvedPath { get; init; }

    /// <summary>Gets the extern aliases the reference declares.</summary>
    public ImmutableArray<string> Aliases
    {
        get => field;
        init => field = ImmutableArrays.OrEmpty(value);
    } = [];

    /// <summary>
    /// Gets whether the assembly is copied to the output directory, when the reference says.
    /// <see langword="null"/> means it did not say, which is not the same as <c>false</c>.
    /// </summary>
    public bool? Private { get; init; }

    /// <summary>Gets the MSBuild condition guarding the reference, when it has one.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets any additional declared metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the assembly name.</summary>
    /// <returns>Something like <c>Reference: System.Xml</c>.</returns>
    public override string ToString() => $"Reference: {Name}";
}
