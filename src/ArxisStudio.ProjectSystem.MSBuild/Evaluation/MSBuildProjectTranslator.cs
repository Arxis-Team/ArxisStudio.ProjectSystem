using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Turns an evaluation into a core snapshot.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over <see cref="EvaluatedProject"/>: no MSBuild, no file system, no clock. That
/// is what lets the mapping be tested exhaustively and in milliseconds, which matters because the
/// mapping is where the bugs are. Everything that needs MSBuild lives in the adapter that produces
/// the input.
/// </para>
/// <para>
/// Two things it deliberately does not do. It does not guess: a property MSBuild did not set
/// becomes an absent value rather than a default, because "the provider did not say" and "the
/// project says zero" are different answers and a consumer can tell them apart. And it does not
/// interpret: a package version is carried across as the text the project wrote, because parsing
/// NuGet version ranges is NuGet's job and getting it subtly wrong here would be worse than not
/// doing it.
/// </para>
/// </remarks>
internal static class MSBuildProjectTranslator
{
    /// <summary>Translates one evaluated project.</summary>
    /// <param name="project">What the evaluation produced.</param>
    /// <param name="workspace">The workspace the snapshot belongs to.</param>
    /// <param name="providerName">The provider to attribute it to.</param>
    /// <param name="knownProjects">
    /// The project files this workspace is loading, which is what turns a project reference into a
    /// resolved identity. Empty when a standalone project is being opened: nothing is then known
    /// about which other projects exist, and an identity minted anyway would be one for a project
    /// nobody loaded.
    /// </param>
    /// <param name="resolvedPackages">
    /// What restore resolved for this framework, when the provider found and read it. Empty is the
    /// honest answer for a project that has not been restored, and is not the same as a project
    /// with no packages -- which is why a missing restore is also reported as a diagnostic.
    /// </param>
    /// <param name="diagnostics">Anything the provider noticed while gathering the above.</param>
    /// <returns>The snapshot.</returns>
    internal static ProjectSnapshot Translate(
        EvaluatedProject project,
        WorkspaceIdentity workspace,
        string providerName,
        IReadOnlySet<CanonicalPath>? knownProjects = null,
        ImmutableArray<ResolvedPackage> resolvedPackages = default,
        IReadOnlyList<ProjectDiagnostic>? diagnostics = null)
    {
        CanonicalPath directory = project.FullPath.Directory;

        var builder = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, project.FullPath),
            Name = Property(project, "MSBuildProjectName")
                ?? Property(project, "AssemblyName")
                ?? System.IO.Path.GetFileNameWithoutExtension(project.FullPath.FileName),
            ProjectFilePath = project.FullPath,
            ProviderName = providerName,
            Language = MSBuildWellKnown.LanguagesByExtension.GetValueOrDefault(project.FullPath.Extension),
            Kind = Property(project, "OutputType"),
            ActiveConfiguration = Property(project, "Configuration"),
            ActivePlatform = Property(project, "Platform"),
            ActiveTargetFramework = Property(project, "TargetFramework"),
        };

        AddRange(builder.TargetFrameworks, TargetFrameworks(project));
        AddRange(builder.Configurations, List(project, "Configurations"));
        AddRange(builder.Platforms, List(project, "Platforms"));

        foreach (KeyValuePair<string, string> property in project.Properties)
        {
            if (MSBuildWellKnown.SurfacedProperties.Contains(property.Key))
            {
                builder.Properties[property.Key] = property.Value;
            }
        }

        foreach (EvaluatedItem item in project.Items)
        {
            Translate(item, builder, directory, workspace, knownProjects);
        }

        foreach (OutputArtifact artifact in Outputs(project, directory))
        {
            builder.Outputs.Add(artifact);
        }

        if (!resolvedPackages.IsDefault)
        {
            foreach (ResolvedPackage package in resolvedPackages)
            {
                builder.ResolvedPackages.Add(package);
            }
        }

        if (diagnostics is not null)
        {
            foreach (ProjectDiagnostic diagnostic in diagnostics)
            {
                builder.Diagnostics.Add(diagnostic);
            }
        }

        return builder.ToSnapshot();
    }

    private static void Translate(
        EvaluatedItem item,
        ProjectSnapshotBuilder builder,
        CanonicalPath directory,
        WorkspaceIdentity workspace,
        IReadOnlySet<CanonicalPath>? knownProjects)
    {
        if (string.Equals(item.ItemType, MSBuildWellKnown.ProjectReference, StringComparison.OrdinalIgnoreCase))
        {
            CanonicalPath referenced = item.FullPath.IsEmpty
                ? Resolve(directory, item.EvaluatedInclude)
                : item.FullPath;

            if (referenced.IsEmpty)
            {
                return;
            }

            builder.ProjectReferences.Add(new ProjectReferenceInfo
            {
                ProjectFilePath = referenced,

                // An identity only when the target is one of the projects this workspace is
                // loading. A reference can perfectly well point outside the solution, and giving
                // that an identity would invent a project nobody opened.
                Project = knownProjects?.Contains(referenced) == true
                    ? ProjectIdentity.Create(workspace, referenced)
                    : ProjectIdentity.None,
                Aliases = [.. Aliases(item.Metadata.GetValueOrDefault("Aliases"))],
                ReferenceOutputAssembly = Boolean(item.Metadata.GetValueOrDefault("ReferenceOutputAssembly")),
                Metadata = item.Metadata,
            });

            return;
        }

        if (string.Equals(item.ItemType, MSBuildWellKnown.PackageReference, StringComparison.OrdinalIgnoreCase))
        {
            builder.PackageReferences.Add(new PackageReferenceInfo
            {
                PackageId = item.EvaluatedInclude,
                VersionText = item.Metadata.GetValueOrDefault("Version"),
                PrivateAssets = item.Metadata.GetValueOrDefault("PrivateAssets"),
                IncludeAssets = item.Metadata.GetValueOrDefault("IncludeAssets"),
                ExcludeAssets = item.Metadata.GetValueOrDefault("ExcludeAssets"),
                Metadata = item.Metadata,
            });

            return;
        }

        if (string.Equals(item.ItemType, MSBuildWellKnown.FrameworkReference, StringComparison.OrdinalIgnoreCase))
        {
            builder.FrameworkReferences.Add(new FrameworkReferenceInfo
            {
                Name = item.EvaluatedInclude,
                Metadata = item.Metadata,
            });

            return;
        }

        if (string.Equals(item.ItemType, MSBuildWellKnown.Reference, StringComparison.OrdinalIgnoreCase))
        {
            builder.AssemblyReferences.Add(new AssemblyReferenceInfo
            {
                Name = item.EvaluatedInclude,
                HintPath = Resolve(directory, item.Metadata.GetValueOrDefault("HintPath")),
                ResolvedPath = item.FullPath,
                Aliases = [.. Aliases(item.Metadata.GetValueOrDefault("Aliases"))],
                Private = Boolean(item.Metadata.GetValueOrDefault("Private")),
                Metadata = item.Metadata,
            });

            return;
        }

        if (string.Equals(item.ItemType, MSBuildWellKnown.Analyzer, StringComparison.OrdinalIgnoreCase))
        {
            CanonicalPath assembly = item.FullPath.IsEmpty
                ? Resolve(directory, item.EvaluatedInclude)
                : item.FullPath;

            if (assembly.IsEmpty)
            {
                return;
            }

            builder.AnalyzerReferences.Add(new AnalyzerReferenceInfo
            {
                AssemblyPath = assembly,
                Metadata = item.Metadata,
            });

            return;
        }

        builder.Items.Add(new ProjectItem
        {
            ItemType = item.ItemType,
            Include = item.EvaluatedInclude,
            FullPath = item.FullPath,
            Link = item.Metadata.GetValueOrDefault("Link"),
            Metadata = item.Metadata,
            Origin = item.IsImported ? ProjectItemOrigin.Imported : ProjectItemOrigin.Declared,
        });
    }

    /// <summary>
    /// The frameworks the project declares, preferring the plural property.
    /// </summary>
    /// <remarks>
    /// A cross-targeting project sets <c>TargetFrameworks</c> and, inside a per-framework
    /// evaluation, also sets <c>TargetFramework</c> to whichever one is being evaluated. Reading
    /// the singular first would report a cross-targeting project as targeting one thing.
    /// </remarks>
    private static IEnumerable<string> TargetFrameworks(EvaluatedProject project)
    {
        IReadOnlyList<string> plural = List(project, "TargetFrameworks");

        if (plural.Count > 0)
        {
            return plural;
        }

        return Property(project, "TargetFramework") is { } single ? [single] : [];
    }

    /// <summary>
    /// What the build will put on disk, read from the evaluated context that decides it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the artifacts a consumer needs to assemble a runtime environment, so the promise is
    /// "what this build produces", not "what is there now". Nothing is checked for existence: a
    /// snapshot taken before a build would then report nothing, and one taken after would silently
    /// mean something different.
    /// </para>
    /// <para>
    /// Each is therefore gated on what decides whether the build emits it at all, and the gates are
    /// not uniform — which was measured rather than assumed. <c>TargetRefPath</c> empties itself when
    /// <c>ProduceReferenceAssembly</c> is false, so the path is its own gate. But
    /// <c>ProjectRuntimeConfigFilePath</c> is populated for an ordinary library that emits no
    /// <c>runtimeconfig.json</c> at all, so that one needs
    /// <c>GenerateRuntimeConfigurationFiles</c> to be asked as well. Reporting it regardless would
    /// name a file that never appears — the same mistake as taking <c>bin/placeholder/</c>
    /// literally, which <see href="../../docs/adr/0012-restore-assets-are-read-not-resolved.md">ADR 0012</see>
    /// records from the restore side.
    /// </para>
    /// </remarks>
    private static IEnumerable<OutputArtifact> Outputs(EvaluatedProject project, CanonicalPath directory)
    {
        string? framework = Property(project, "TargetFramework");
        string? targetPath = Property(project, "TargetPath");

        if (Resolve(directory, targetPath) is { IsEmpty: false } assembly)
        {
            yield return Artifact(OutputArtifactKind.Assembly, assembly, framework);
        }

        // Symbols are the one artifact with no property naming them: the SDK composes the path
        // inside a target, from the same TargetDir and TargetName that make up TargetPath. So this
        // composes it too, gated on DebugType, which is what decides whether one is emitted.
        // A project that redirects its symbols elsewhere is the documented cost.
        if (targetPath is not null && Property(project, "DebugType") is { } debugType
            && !string.Equals(debugType, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (Resolve(directory, System.IO.Path.ChangeExtension(targetPath, ".pdb")) is
                { IsEmpty: false } symbols)
            {
                yield return Artifact(OutputArtifactKind.SymbolFile, symbols, framework);
            }
        }

        if (Resolve(directory, Property(project, "TargetRefPath")) is { IsEmpty: false } reference)
        {
            yield return Artifact(OutputArtifactKind.ReferenceAssembly, reference, framework);
        }

        if (Boolean(Property(project, "GenerateDependencyFile")) == true
            && Resolve(directory, Property(project, "ProjectDepsFilePath")) is { IsEmpty: false } dependencies)
        {
            yield return Artifact(OutputArtifactKind.DependencyManifest, dependencies, framework);
        }

        if (Boolean(Property(project, "GenerateRuntimeConfigurationFiles")) == true
            && Resolve(directory, Property(project, "ProjectRuntimeConfigFilePath")) is
                { IsEmpty: false } runtimeConfiguration)
        {
            yield return Artifact(OutputArtifactKind.RuntimeConfiguration, runtimeConfiguration, framework);
        }

        if (Resolve(directory, Property(project, "DocumentationFile")) is { IsEmpty: false } documentation)
        {
            yield return Artifact(OutputArtifactKind.DocumentationFile, documentation, framework);
        }
    }

    private static OutputArtifact Artifact(OutputArtifactKind kind, CanonicalPath path, string? framework) =>
        new()
        {
            Kind = kind,
            Path = path,
            TargetFramework = framework,
        };

    /// <summary>Reads a property, treating blank as absent.</summary>
    private static string? Property(EvaluatedProject project, string name)
    {
        string? value = project.Properties.GetValueOrDefault(name);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Splits a semicolon-separated property, dropping blanks and duplicates.</summary>
    private static IReadOnlyList<string> List(EvaluatedProject project, string name)
    {
        if (Property(project, name) is not { } value)
        {
            return [];
        }

        return [.. value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Splits extern aliases, which MSBuild writes separated by commas or semicolons depending on
    /// who wrote the project file.
    /// </summary>
    private static string[] Aliases(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parses an MSBuild boolean, where anything unrecognised means the project did not say.
    /// </summary>
    private static bool? Boolean(string? value) =>
        bool.TryParse(value, out bool parsed) ? parsed : null;

    /// <summary>Resolves a possibly-relative path against the project directory.</summary>
    private static CanonicalPath Resolve(CanonicalPath directory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || directory.IsEmpty)
        {
            return CanonicalPath.None;
        }

        try
        {
            return CanonicalPath.Create(directory, value);
        }
        catch (ArgumentException)
        {
            // A property that is not a usable path -- a wildcard, a leftover token, a value some
            // target built out of pieces. Not worth a diagnostic: the project is fine, this one
            // string simply is not a path, and inventing one would be worse than having none.
            return CanonicalPath.None;
        }
    }

    private static void AddRange(IList<string> target, IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            target.Add(value);
        }
    }
}
