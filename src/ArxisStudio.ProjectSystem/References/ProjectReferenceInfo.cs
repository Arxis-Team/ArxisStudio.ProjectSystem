using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A reference from one project to another, as the referencing project declares it.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Info</c> suffix on this and its four siblings is load-bearing. <c>ProjectReference</c> is
/// an MSBuild item name and <c>AnalyzerReference</c> is a Roslyn class that <em>loads</em>
/// analyzers; a consumer with <c>using Microsoft.CodeAnalysis;</c> in scope would find the plain
/// names ambiguous at best and misleading at worst. The suffix says "a description of, which does
/// nothing" — which is exactly the guarantee this package makes.
/// </para>
/// <para>
/// <see cref="Project"/> is <see cref="ProjectIdentity.None"/> when the target is not among the
/// projects the workspace loaded, which is normal: a solution need not contain everything its
/// projects reference.
/// </para>
/// </remarks>
public sealed record ProjectReferenceInfo
{
    /// <summary>Gets the referenced project file.</summary>
    public required CanonicalPath ProjectFilePath { get; init; }

    /// <summary>Gets the referenced project's identity, when it is loaded in this workspace.</summary>
    public ProjectIdentity Project { get; init; }

    /// <summary>Gets the extern aliases the reference declares.</summary>
    public ImmutableArray<string> Aliases
    {
        get => field;
        init => field = ImmutableArrays.OrEmpty(value);
    } = [];

    /// <summary>
    /// Gets whether the referenced project's output is passed to the compiler, when the reference
    /// says. <see langword="null"/> means it did not say, which is not the same as <c>false</c>.
    /// </summary>
    public bool? ReferenceOutputAssembly { get; init; }

    /// <summary>Gets the MSBuild condition guarding the reference, when it has one.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets any additional declared metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the referenced project file name.</summary>
    /// <returns>Something like <c>ProjectReference: Core.csproj</c>.</returns>
    public override string ToString() => $"ProjectReference: {ProjectFilePath.FileName}";
}
