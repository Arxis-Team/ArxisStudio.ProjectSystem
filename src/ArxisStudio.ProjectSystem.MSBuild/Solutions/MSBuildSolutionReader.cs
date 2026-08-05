using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Reads a <c>.sln</c> or <c>.slnx</c> and copies out what it lists.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>Microsoft.VisualStudio.SolutionPersistence</c> rather than MSBuild's own
/// <c>SolutionFile</c>, and the reason is measured rather than stylistic: <c>SolutionFile</c> loses
/// solution folders entirely for <c>.slnx</c>. Projects come back carrying a parent folder guid
/// while the folder appears in neither <c>ProjectsInOrder</c> nor <c>ProjectsByGuid</c>, so the
/// reference resolves to nothing and a folder tree built from it has a hole in it. This library
/// returns identical, complete results for both formats, and is the one the SDK itself uses.
/// </para>
/// <para>
/// It also has nothing to do with MSBuild, so unlike <see cref="MSBuildProjectEvaluator"/> it may be
/// entered before the locator has run. Nothing depends on that, but it is worth knowing when
/// reading the ordering rule elsewhere in this package.
/// </para>
/// </remarks>
internal static class MSBuildSolutionReader
{
    /// <summary>Reads a solution file, or explains why it could not be read.</summary>
    /// <param name="solutionPath">The solution file.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What the solution lists, or the reason it could not be read.</returns>
    internal static async ValueTask<(DiscoveredSolution? Solution, string? Error)> ReadAsync(
        CanonicalPath solutionPath,
        CancellationToken cancellationToken)
    {
        ISolutionSerializer? serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath.Value);

        if (serializer is null)
        {
            return (null, $"'{solutionPath.Extension}' is not a solution format this provider can read.");
        }

        SolutionModel model;

        try
        {
            model = await serializer.OpenAsync(solutionPath.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Malformed solutions are ordinary, so the reason travels back as text rather than as
            // an exception the provider would have to catch a parser-specific type to handle.
            return (null, $"{exception.GetType().Name}: {exception.Message}");
        }

        CanonicalPath directory = solutionPath.Directory;

        return (
            new DiscoveredSolution
            {
                FullPath = solutionPath,
                Name = System.IO.Path.GetFileNameWithoutExtension(solutionPath.FileName),
                Projects = Projects(model, directory),
                Folders = Folders(model),
                Configurations = [.. model.BuildTypes],
                Platforms = [.. model.Platforms],
            },
            null);
    }

    private static ImmutableArray<DiscoveredProject> Projects(SolutionModel model, CanonicalPath directory)
    {
        ImmutableArray<DiscoveredProject>.Builder projects =
            ImmutableArray.CreateBuilder<DiscoveredProject>(model.SolutionProjects.Count);

        foreach (SolutionProjectModel project in model.SolutionProjects)
        {
            // The solution stores a relative path, and resolving it against the solution directory
            // rather than the working directory is what keeps the answer independent of where the
            // host was started.
            if (!TryResolve(directory, project.FilePath, out CanonicalPath path))
            {
                continue;
            }

            projects.Add(new DiscoveredProject
            {
                FullPath = path,
                Name = project.ActualDisplayName,
                FolderPath = project.Parent?.Path,
            });
        }

        return projects.ToImmutable();
    }

    private static ImmutableArray<DiscoveredFolder> Folders(SolutionModel model)
    {
        ImmutableArray<DiscoveredFolder>.Builder folders =
            ImmutableArray.CreateBuilder<DiscoveredFolder>(model.SolutionFolders.Count);

        foreach (SolutionFolderModel folder in model.SolutionFolders)
        {
            folders.Add(new DiscoveredFolder
            {
                Name = folder.ActualDisplayName,
                Path = folder.Path,
            });
        }

        return folders.ToImmutable();
    }

    private static bool TryResolve(CanonicalPath directory, string? relative, out CanonicalPath path)
    {
        path = CanonicalPath.None;

        if (directory.IsEmpty || string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        try
        {
            path = CanonicalPath.Create(directory, relative);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
