using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A complete, immutable picture of one project at one moment.
/// </summary>
/// <remarks>
/// <para>
/// There is no public constructor and no settable property. The only way to make one is
/// <see cref="ProjectSnapshotBuilder"/>, which is what lets this type promise that every collection
/// it exposes is core-owned and safe to enumerate — a provider cannot hand over a list and keep
/// mutating it, because it never hands over a list at all.
/// </para>
/// <para>
/// Every collection is empty rather than <see langword="null"/>, and never a
/// <c>default(ImmutableArray&lt;T&gt;)</c>. Not every provider populates every collection; an empty
/// one means "this provider did not supply these", not "this project has none".
/// </para>
/// <para>
/// <b>Equality is reference equality</b>, deliberately. A generated structural <c>Equals</c> would
/// walk every item of a project that can have tens of thousands, and so would its hash — which
/// makes using one as a dictionary key quietly catastrophic. The intended staleness check is
/// <see cref="Identity"/> plus the owning snapshot's <see cref="SolutionSnapshot.Version"/>, and
/// that is a comparison of two small values. The leaf types — items, references, artifacts,
/// diagnostics, metadata — do have value equality, because comparing those is genuinely useful.
/// </para>
/// </remarks>
public sealed class ProjectSnapshot
{
    internal ProjectSnapshot(
        ProjectIdentity identity,
        string name,
        CanonicalPath projectFilePath,
        string? language,
        string? kind,
        string? providerName,
        string? activeConfiguration,
        string? activePlatform,
        string? activeTargetFramework,
        ImmutableArray<string> targetFrameworks,
        ImmutableArray<string> configurations,
        ImmutableArray<string> platforms,
        ImmutableArray<ProjectReferenceInfo> projectReferences,
        ImmutableArray<PackageReferenceInfo> packageReferences,
        ImmutableArray<FrameworkReferenceInfo> frameworkReferences,
        ImmutableArray<AssemblyReferenceInfo> assemblyReferences,
        ImmutableArray<AnalyzerReferenceInfo> analyzerReferences,
        ImmutableArray<ProjectItem> items,
        ImmutableArray<OutputArtifact> outputs,
        ImmutableArray<ProjectDiagnostic> diagnostics,
        ProjectMetadata properties)
    {
        Identity = identity;
        Name = name;
        ProjectFilePath = projectFilePath;

        // Derived, never supplied: a directory that could disagree with the project file is a
        // disagreement waiting to be shipped.
        ProjectDirectory = projectFilePath.Directory;

        Language = language;
        Kind = kind;
        ProviderName = providerName;
        ActiveConfiguration = activeConfiguration;
        ActivePlatform = activePlatform;
        ActiveTargetFramework = activeTargetFramework;
        TargetFrameworks = targetFrameworks;
        Configurations = configurations;
        Platforms = platforms;
        ProjectReferences = projectReferences;
        PackageReferences = packageReferences;
        FrameworkReferences = frameworkReferences;
        AssemblyReferences = assemblyReferences;
        AnalyzerReferences = analyzerReferences;
        Items = items;
        Outputs = outputs;
        Diagnostics = diagnostics;
        Properties = properties;
    }

    /// <summary>Gets the project's identity within its workspace.</summary>
    public ProjectIdentity Identity { get; }

    /// <summary>Gets the project's display name.</summary>
    public string Name { get; }

    /// <summary>Gets the canonical path of the project file.</summary>
    public CanonicalPath ProjectFilePath { get; }

    /// <summary>Gets the directory containing the project file.</summary>
    public CanonicalPath ProjectDirectory { get; }

    /// <summary>Gets a neutral language label such as <c>C#</c>, when the provider supplied one.</summary>
    public string? Language { get; }

    /// <summary>Gets a neutral project-kind label, when the provider supplied one.</summary>
    public string? Kind { get; }

    /// <summary>Gets the name of the provider that produced this snapshot.</summary>
    public string? ProviderName { get; }

    /// <summary>Gets the configuration this snapshot was evaluated under, when one applied.</summary>
    public string? ActiveConfiguration { get; }

    /// <summary>Gets the platform this snapshot was evaluated under, when one applied.</summary>
    public string? ActivePlatform { get; }

    /// <summary>Gets the target framework this snapshot was evaluated under, when the project cross-targets.</summary>
    public string? ActiveTargetFramework { get; }

    /// <summary>Gets every target framework the project declares.</summary>
    public ImmutableArray<string> TargetFrameworks { get; }

    /// <summary>Gets every configuration the project declares.</summary>
    public ImmutableArray<string> Configurations { get; }

    /// <summary>Gets every platform the project declares.</summary>
    public ImmutableArray<string> Platforms { get; }

    /// <summary>Gets the projects this one references.</summary>
    public ImmutableArray<ProjectReferenceInfo> ProjectReferences { get; }

    /// <summary>Gets the packages this project declares.</summary>
    public ImmutableArray<PackageReferenceInfo> PackageReferences { get; }

    /// <summary>Gets the shared frameworks this project references.</summary>
    public ImmutableArray<FrameworkReferenceInfo> FrameworkReferences { get; }

    /// <summary>Gets the assemblies this project references directly.</summary>
    public ImmutableArray<AssemblyReferenceInfo> AssemblyReferences { get; }

    /// <summary>Gets the analyzers this project loads.</summary>
    public ImmutableArray<AnalyzerReferenceInfo> AnalyzerReferences { get; }

    /// <summary>Gets the project's items.</summary>
    public ImmutableArray<ProjectItem> Items { get; }

    /// <summary>Gets the artifacts a provider found, when a build had produced any.</summary>
    public ImmutableArray<OutputArtifact> Outputs { get; }

    /// <summary>Gets the diagnostics raised about this project.</summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the evaluated properties a provider chose to surface.
    /// </summary>
    /// <remarks>
    /// Keys compare case-insensitively, as MSBuild property names do. This is the deliberate
    /// escape hatch: anything provider-specific goes here rather than growing a public property
    /// the whole package would then have to keep.
    /// </remarks>
    public ProjectMetadata Properties { get; }

    /// <summary>Gets a value indicating whether any diagnostic about this project is an error.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (ProjectDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.IsError)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Returns the project's name and file.</summary>
    /// <returns>Something like <c>App (C:\src\App\App.csproj)</c>.</returns>
    public override string ToString() => $"{Name} ({ProjectFilePath})";
}
