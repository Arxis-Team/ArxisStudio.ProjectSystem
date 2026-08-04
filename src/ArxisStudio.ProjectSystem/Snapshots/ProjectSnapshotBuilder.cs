using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Assembles a <see cref="ProjectSnapshot"/>. A provider fills one of these in and calls
/// <see cref="ToSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread-safe.</b> A builder belongs to whatever is filling it in; two threads filling one
/// builder is a bug in the provider, not a case this type handles.
/// </para>
/// <para>
/// This exists so that defensive copying happens exactly once, at the type boundary, rather than
/// repeatedly at every hand-off. The builder owns ordinary mutable lists; <see cref="ToSnapshot"/>
/// copies each into an immutable array the core owns. A provider can go on mutating its builder
/// afterwards — or call <see cref="ToSnapshot"/> twice — without any snapshot it already produced
/// noticing.
/// </para>
/// </remarks>
public sealed class ProjectSnapshotBuilder
{
    /// <summary>Gets or sets the project's identity within its workspace.</summary>
    public ProjectIdentity Identity { get; set; }

    /// <summary>Gets or sets the project's display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the canonical path of the project file.</summary>
    public CanonicalPath ProjectFilePath { get; set; }

    /// <summary>Gets or sets a neutral language label such as <c>C#</c>.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets a neutral project-kind label.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the name of the provider producing this snapshot.</summary>
    public string? ProviderName { get; set; }

    /// <summary>Gets or sets the configuration the project was evaluated under.</summary>
    public string? ActiveConfiguration { get; set; }

    /// <summary>Gets or sets the platform the project was evaluated under.</summary>
    public string? ActivePlatform { get; set; }

    /// <summary>Gets or sets the target framework the project was evaluated under.</summary>
    public string? ActiveTargetFramework { get; set; }

    /// <summary>Gets the target frameworks the project declares.</summary>
    public IList<string> TargetFrameworks { get; } = [];

    /// <summary>Gets the configurations the project declares.</summary>
    public IList<string> Configurations { get; } = [];

    /// <summary>Gets the platforms the project declares.</summary>
    public IList<string> Platforms { get; } = [];

    /// <summary>Gets the projects this one references.</summary>
    public IList<ProjectReferenceInfo> ProjectReferences { get; } = [];

    /// <summary>Gets the packages this project declares.</summary>
    public IList<PackageReferenceInfo> PackageReferences { get; } = [];

    /// <summary>Gets the shared frameworks this project references.</summary>
    public IList<FrameworkReferenceInfo> FrameworkReferences { get; } = [];

    /// <summary>Gets the assemblies this project references directly.</summary>
    public IList<AssemblyReferenceInfo> AssemblyReferences { get; } = [];

    /// <summary>Gets the analyzers this project loads.</summary>
    public IList<AnalyzerReferenceInfo> AnalyzerReferences { get; } = [];

    /// <summary>Gets the project's items.</summary>
    public IList<ProjectItem> Items { get; } = [];

    /// <summary>Gets the artifacts a build produced.</summary>
    public IList<OutputArtifact> Outputs { get; } = [];

    /// <summary>Gets the diagnostics raised about this project.</summary>
    public IList<ProjectDiagnostic> Diagnostics { get; } = [];

    /// <summary>Gets the evaluated properties to surface. Keys compare case-insensitively.</summary>
    public IDictionary<string, string> Properties { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Materialises an immutable snapshot from the current contents.</summary>
    /// <returns>The snapshot.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder is not complete enough to describe a project: no identity, no name, no project
    /// file, or a project file that disagrees with the identity. These are broken invariants in
    /// provider code and are caught there — a workspace separately turns any provider exception
    /// into a diagnostic, so a consumer still never sees this.
    /// </exception>
    public ProjectSnapshot ToSnapshot()
    {
        if (Identity.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{nameof(Identity)} must be set before a project snapshot can be built.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException(
                $"{nameof(Name)} must be set before a project snapshot can be built.");
        }

        if (ProjectFilePath.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{nameof(ProjectFilePath)} must be set before a project snapshot can be built.");
        }

        if (ProjectFilePath != Identity.ProjectFilePath)
        {
            throw new InvalidOperationException(
                $"{nameof(ProjectFilePath)} is '{ProjectFilePath}' but {nameof(Identity)} was created for " +
                $"'{Identity.ProjectFilePath}'. An identity derived from a different file than the snapshot " +
                "describes would make the same project appear twice.");
        }

        return new ProjectSnapshot(
            Identity,
            Name,
            ProjectFilePath,
            Language,
            Kind,
            ProviderName,
            ActiveConfiguration,
            ActivePlatform,
            ActiveTargetFramework,
            [.. TargetFrameworks],
            [.. Configurations],
            [.. Platforms],
            [.. ProjectReferences],
            [.. PackageReferences],
            [.. FrameworkReferences],
            [.. AssemblyReferences],
            [.. AnalyzerReferences],
            [.. Items],
            [.. Outputs],
            [.. Diagnostics],
            ProjectMetadata.Create(Properties));
    }
}
