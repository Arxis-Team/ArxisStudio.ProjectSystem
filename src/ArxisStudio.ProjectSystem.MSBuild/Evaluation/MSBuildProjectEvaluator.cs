using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Evaluates a project file and copies the result out.
/// </summary>
/// <remarks>
/// <para>
/// The only type in this package that names <c>Microsoft.Build.Evaluation</c>, and deliberately so.
/// The runtime loads an assembly when a method naming its types is first entered, so keeping those
/// names in one type that nothing enters before <see cref="MSBuildEnvironment"/> has registered is
/// what makes the ordering rule structural rather than a thing to remember.
/// <see cref="MethodImplOptions.NoInlining"/> on the entry point stops the JIT from undoing that by
/// folding this into its caller.
/// </para>
/// <para>
/// It is also deliberately dull. Everything interesting happens in
/// <see cref="MSBuildProjectTranslator"/>, which is a pure function and is tested properly; this
/// copies fields and is covered by a handful of integration tests. Logic that drifted in here would
/// be logic that costs an SDK to test.
/// </para>
/// <para>
/// The <see cref="ProjectCollection"/> is created per evaluation and disposed before this returns.
/// MSBuild's evaluation objects belong to their collection and stop being readable once it goes, so
/// everything wanted is copied into core-owned values first — which is the same rule
/// <c>docs/adr/0003</c> states, applied at the one place it actually bites.
/// </para>
/// </remarks>
internal static class MSBuildProjectEvaluator
{
    /// <summary>
    /// Evaluates a project, or explains why it could not be.
    /// </summary>
    /// <remarks>
    /// A <c>Try</c> shape rather than an exception, for two reasons that point the same way. A
    /// malformed project is an <em>expected</em> outcome in this library, and expected outcomes are
    /// not exceptions. And the exception MSBuild raises is an MSBuild type: catching it in the
    /// provider would name <c>Microsoft.Build.Exceptions</c> there and load the assembly before the
    /// locator had run, which is the exact ordering mistake this file exists to prevent. The failure
    /// is turned into a string here, where MSBuild is already loaded and allowed.
    /// </remarks>
    /// <param name="projectPath">The project file.</param>
    /// <param name="globalProperties">Properties to evaluate with.</param>
    /// <param name="includeItems">Whether items other than references are wanted.</param>
    /// <param name="evaluated">The evaluation, owned by this library rather than by MSBuild.</param>
    /// <param name="error">Why the evaluation failed, when it did.</param>
    /// <returns><see langword="true"/> when the project evaluated.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TryEvaluate(
        CanonicalPath projectPath,
        IReadOnlyDictionary<string, string> globalProperties,
        bool includeItems,
        out EvaluatedProject? evaluated,
        out string? error)
    {
        evaluated = null;
        error = null;

        // A collection of its own, disposed below. Sharing one across evaluations would share its
        // caches and its lifetime, and a stale cached project is a wrong answer that looks right.
        using var collection = new ProjectCollection(
            new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase));

        try
        {
            Project project = Project.FromFile(
                projectPath.Value,
                new ProjectOptions
                {
                    ProjectCollection = collection,
                    LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                        | ProjectLoadSettings.IgnoreInvalidImports
                        | ProjectLoadSettings.IgnoreEmptyImports,
                });

            evaluated = new EvaluatedProject
            {
                FullPath = projectPath,
                Properties = Properties(project),
                Items = Items(project, projectPath, includeItems),
            };

            return true;
        }
        catch (InvalidProjectFileException exception)
        {
            // MSBuild's own message is carried through because it is usually the useful part: it
            // names the import that is missing or the element that is malformed.
            error = exception.Message;

            return false;
        }
    }

    private static ProjectMetadata Properties(Project project)
    {
        var properties = new List<KeyValuePair<string, string>>();

        foreach (ProjectProperty property in project.Properties)
        {
            // Only what the snapshot will surface. Copying a thousand properties out of MSBuild to
            // throw all but thirty away is work with a cost and no reader.
            if (MSBuildWellKnown.SurfacedProperties.Contains(property.Name))
            {
                properties.Add(new KeyValuePair<string, string>(property.Name, property.EvaluatedValue));
            }
        }

        return ProjectMetadata.Create(properties);
    }

    private static ImmutableArray<EvaluatedItem> Items(
        Project project,
        CanonicalPath projectPath,
        bool includeItems)
    {
        CanonicalPath directory = projectPath.Directory;
        ImmutableArray<EvaluatedItem>.Builder items = ImmutableArray.CreateBuilder<EvaluatedItem>();

        foreach (Microsoft.Build.Evaluation.ProjectItem item in project.Items)
        {
            bool isReference = MSBuildWellKnown.ReferenceItemTypes.Contains(item.ItemType);

            // The option is a permission to skip expensive work, not a promise to a consumer. The
            // references are never skipped: they are the point of opening a project at all.
            if (!includeItems && !isReference)
            {
                continue;
            }

            items.Add(new EvaluatedItem
            {
                ItemType = item.ItemType,
                EvaluatedInclude = item.EvaluatedInclude,
                FullPath = FullPath(item, directory),
                Metadata = Metadata(item),
                IsImported = item.IsImported,
            });
        }

        return items.ToImmutable();
    }

    /// <summary>
    /// The item's resolved location, when it has one.
    /// </summary>
    /// <remarks>
    /// Resolved lexically against the project directory rather than through MSBuild's well-known
    /// <c>%(FullPath)</c>, which is computed against the current working directory and would make
    /// the answer depend on where the host happened to be started.
    /// </remarks>
    private static CanonicalPath FullPath(Microsoft.Build.Evaluation.ProjectItem item, CanonicalPath directory)
    {
        if (directory.IsEmpty || string.IsNullOrWhiteSpace(item.EvaluatedInclude))
        {
            return CanonicalPath.None;
        }

        try
        {
            return CanonicalPath.Create(directory, item.EvaluatedInclude);
        }
        catch (ArgumentException)
        {
            // Plenty of item types do not name files -- a PackageReference is a package id, a
            // FrameworkReference is a framework name. Not having a path is normal here.
            return CanonicalPath.None;
        }
    }

    private static ProjectMetadata Metadata(Microsoft.Build.Evaluation.ProjectItem item)
    {
        if (item.DirectMetadataCount == 0 && item.Metadata.Count == 0)
        {
            return ArxisStudio.ProjectSystem.ProjectMetadata.Empty;
        }

        var metadata = new List<KeyValuePair<string, string>>(item.Metadata.Count);

        // item.Metadata is what the project and its item definitions wrote. The well-known computed
        // metadata -- FullPath, Filename, ModifiedTime and the rest -- is deliberately not here:
        // it is derivable, some of it touches the disk, and none of it belongs in a snapshot.
        foreach (Microsoft.Build.Evaluation.ProjectMetadata entry in item.Metadata)
        {
            metadata.Add(new KeyValuePair<string, string>(entry.Name, entry.EvaluatedValue));
        }

        return ArxisStudio.ProjectSystem.ProjectMetadata.Create(metadata);
    }
}
