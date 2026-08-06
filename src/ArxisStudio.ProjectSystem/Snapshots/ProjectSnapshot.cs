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
        ImmutableArray<ResolvedPackage> resolvedPackages,
        ImmutableArray<ProjectItem> items,
        ImmutableArray<OutputArtifact> outputs,
        ImmutableArray<CanonicalPath> evaluationInputs,
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
        ResolvedPackages = resolvedPackages;
        Items = items;
        Outputs = outputs;
        EvaluationInputs = evaluationInputs;
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

    /// <summary>
    /// Gets the packages restore resolved for <see cref="ActiveTargetFramework"/>, including the
    /// ones nothing declared directly, with the assemblies each contributes.
    /// </summary>
    /// <remarks>
    /// Empty when nothing has been restored, or when the provider did not look. That is not the
    /// same as a project having no packages, and a consumer that needs to tell the difference
    /// should read the diagnostics: a project declaring packages whose restore output is missing
    /// says so there.
    /// </remarks>
    public ImmutableArray<ResolvedPackage> ResolvedPackages { get; }

    /// <summary>Gets the project's items.</summary>
    public ImmutableArray<ProjectItem> Items { get; }

    /// <summary>
    /// Gets the artifacts a build of this project produces — the assembly, its symbols, its
    /// reference assembly and its manifests.
    /// </summary>
    /// <remarks>
    /// These describe what a build <em>puts</em> at those paths, not what is there now: nothing is
    /// checked for existence, so a project that has never been built still reports where its output
    /// will land. A consumer that needs to know whether the file is there, and current, asks the
    /// file system — the snapshot deliberately does not, because an answer baked into an immutable
    /// value would start ageing the moment it was taken.
    /// </remarks>
    public ImmutableArray<OutputArtifact> Outputs { get; }

    /// <summary>
    /// Gets the files whose contents produced this snapshot: the project file, everything it
    /// imported, and any restore output that was read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the answer to "when does this snapshot stop being true", and it is what makes
    /// watching a project possible without the watcher having to understand project files. A change
    /// to any of these means the project must be evaluated again; a change to anything else does
    /// not.
    /// </para>
    /// <para>
    /// A provider is expected to leave out the parts of the toolchain a project imports but nobody
    /// edits — the SDK's own targets, build logic shipped inside packages. Those are the
    /// overwhelming majority of what a project imports and none of them change while a solution is
    /// open, so including them would turn a handful of interesting files into hundreds of
    /// uninteresting ones.
    /// </para>
    /// <para>
    /// Some of these name files that are not there. That is deliberate and load-bearing: a project
    /// that has not been restored still lists where its restore output would be, because a restore
    /// creating it is exactly the change that makes the snapshot stale. A list of only the files
    /// that already exist could never report one appearing.
    /// </para>
    /// <para>
    /// The same reasoning covers the files a build system finds by looking rather than by being
    /// told — a <c>Directory.Build.props</c> is found by walking up from the project, so a project
    /// with none above it would otherwise say nothing about one and somebody creating it would
    /// change the evaluation without changing anything the project declared. A provider is expected
    /// to name every place such a file would be looked for, and how far that goes is the provider's
    /// judgement rather than this property's rule.
    /// </para>
    /// </remarks>
    public ImmutableArray<CanonicalPath> EvaluationInputs { get; }

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
