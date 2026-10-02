using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Works out which directories to watch for everything a solution's projects are made of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each project's directory, with everything below it, in one watch.</b> One watch rather than a
/// watch per source folder kept clear of <c>bin</c> and <c>obj</c>: a folder created in the project is
/// heard together with what is put in it, with no gap until the next <c>Watch</c>, and the events of a
/// project come from one watch in the order they happened — which is what lets the coalescer join a
/// file's departure from one folder and its arrival in another into the move they were. The price is
/// that a build's writes are reported too, and <see cref="SolutionSnapshot.Classify"/> discards them by
/// the project's <see cref="ProjectSnapshot.BuildDirectories"/>.
/// </para>
/// <para>
/// <b>Files outside every project directory are watched through theirs</b> — the solution, imports
/// above the projects, files linked in from elsewhere — by the same rules as
/// <see cref="FileWatchPlan"/>, including a directory that is not there yet.
/// </para>
/// <para>
/// Pure, with the existence check injected, like <see cref="FileWatchPlan"/>.
/// </para>
/// </remarks>
internal static class SourceWatchPlan
{
    /// <summary>Plans the watches for a snapshot.</summary>
    /// <param name="snapshot">The solution whose sources to watch.</param>
    /// <param name="directoryExists">Whether a directory is there.</param>
    /// <returns>The directories to watch, each named once, none below another that includes it.</returns>
    internal static ImmutableArray<WatchedDirectory> For(SolutionSnapshot snapshot, Func<CanonicalPath, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(directoryExists);

        CanonicalPath[] roots = [.. snapshot.Projects
            .Select(static project => project.ProjectDirectory)
            .Where(directory => !directory.IsEmpty && directoryExists(directory))
            .Distinct()];

        IEnumerable<CanonicalPath> outside = snapshot.Projects
            .SelectMany(static project => project.EvaluationInputs.Concat(project.Items.Select(static item => item.FullPath)))
            .Prepend(snapshot.EntryPoint.Path)
            .Where(path => !path.IsEmpty && !Array.Exists(roots, root => path.StartsWith(root)));

        WatchedDirectory[] candidates =
        [
            .. roots.Select(static root => new WatchedDirectory(root, IncludeSubdirectories: true)),
            .. FileWatchPlan.For(outside, directoryExists),
        ];

        // A watch that includes subdirectories covers everything below it, so what is below is
        // dropped rather than reported twice; of two watches of one directory the wider one stays.
        ImmutableArray<WatchedDirectory>.Builder plan = ImmutableArray.CreateBuilder<WatchedDirectory>(candidates.Length);

        foreach (WatchedDirectory candidate in candidates)
        {
            bool covered = Array.Exists(candidates, other =>
                other.IncludeSubdirectories
                && candidate.Directory.StartsWith(other.Directory)
                && (candidate.Directory != other.Directory || !candidate.IncludeSubdirectories));

            if (!covered && !plan.Contains(candidate))
            {
                plan.Add(candidate);
            }
        }

        return plan.ToImmutable();
    }
}
