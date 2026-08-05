using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A package restore actually resolved for one target framework, with the assemblies it contributes.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="PackageReferenceInfo"/>, which is what a project <em>declares</em>. A
/// declaration may carry no version at all under central package management, names only the direct
/// dependencies, and says nothing about which files came out. This is the other half: the exact
/// version restore chose, every package it pulled in transitively, and the assemblies selected for
/// the framework in question.
/// </para>
/// <para>
/// <b>Compile and runtime assemblies are separate because they genuinely differ.</b> A package can
/// be compiled against a reference assembly and contribute nothing at run time — which is exactly
/// what <c>ExcludeAssets="runtime"</c> produces, and what this repository's own MSBuild references
/// do. A consumer building a compiler invocation wants <see cref="CompileAssemblies"/>; one building
/// a runtime environment to load controls into wants <see cref="RuntimeAssemblies"/>. Collapsing
/// them into one list would be wrong for both.
/// </para>
/// <para>
/// Both lists are empty when the package contributes no assemblies of that kind, which is normal:
/// analyzer packages, build-only packages and framework-provided ones all look like that.
/// </para>
/// </remarks>
public sealed record ResolvedPackage
{
    /// <summary>Gets the package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets the exact version restore resolved, unlike the range a project may declare.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the assemblies to compile against.</summary>
    public ImmutableArray<CanonicalPath> CompileAssemblies
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the assemblies needed at run time.</summary>
    public ImmutableArray<CanonicalPath> RuntimeAssemblies
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the ids of the packages this one depends on.</summary>
    public ImmutableArray<string> Dependencies
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>
    /// Gets a value indicating whether the project asked for this package itself, rather than
    /// getting it because something else did.
    /// </summary>
    /// <remarks>
    /// A tool showing a dependency tree needs the distinction: the direct ones are what the user
    /// can edit in the project file, and the rest arrived on their own.
    /// </remarks>
    public bool IsDirect { get; init; }

    /// <summary>Returns the package id and version.</summary>
    /// <returns>Something like <c>Serilog 4.1.0</c>.</returns>
    public override string ToString() => $"{PackageId} {Version}";
}
