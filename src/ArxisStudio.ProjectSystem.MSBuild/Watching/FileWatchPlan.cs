using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>One directory to watch, and whether its subdirectories count.</summary>
internal readonly record struct WatchedDirectory(CanonicalPath Directory, bool IncludeSubdirectories);

/// <summary>
/// Works out which directories to watch for a set of files.
/// </summary>
/// <remarks>
/// <para>
/// Files are watched by watching their directories, not individually. A file watcher bound to one
/// file stops being useful the moment that file is replaced rather than edited — which is what most
/// editors do, and what every tool that writes atomically does — and it cannot see the file appear
/// in the first place.
/// </para>
/// <para>
/// Pure, with the existence check injected, so every rule below is tested exactly and without a
/// disk. The same seam as the rest of this package: the part with the interesting decisions in it
/// does not need the thing it is deciding about.
/// </para>
/// </remarks>
internal static class FileWatchPlan
{
    /// <summary>Plans the watches for a set of files.</summary>
    /// <param name="paths">The files to watch. Empty paths are ignored.</param>
    /// <param name="directoryExists">Whether a directory is there.</param>
    /// <returns>The directories to watch, each named once.</returns>
    internal static ImmutableArray<WatchedDirectory> For(
        IEnumerable<CanonicalPath> paths,
        Func<CanonicalPath, bool> directoryExists)
    {
        var recursive = new HashSet<CanonicalPath>();
        var known = new HashSet<CanonicalPath>();
        var ordered = new List<CanonicalPath>();

        foreach (CanonicalPath path in paths)
        {
            if (path.IsEmpty)
            {
                continue;
            }

            CanonicalPath directory = path.Directory;

            if (directory.IsEmpty)
            {
                continue;
            }

            if (directoryExists(directory))
            {
                if (known.Add(directory))
                {
                    ordered.Add(directory);
                }

                continue;
            }

            // The directory is not there yet, which is ordinary rather than exceptional: an
            // unrestored project names obj/project.assets.json and has no obj. A watcher cannot be
            // bound to a directory that does not exist, so the nearest one that does is watched
            // instead -- and that one has to include subdirectories, or the file appearing inside
            // the directory that is also appearing would be invisible.
            if (NearestExisting(directory, directoryExists) is { IsEmpty: false } ancestor)
            {
                recursive.Add(ancestor);

                if (known.Add(ancestor))
                {
                    ordered.Add(ancestor);
                }
            }
        }

        // A recursive watch already covers anything shallow beneath it, so keep the wider one rather
        // than reporting the same change twice.
        ImmutableArray<WatchedDirectory>.Builder plan =
            ImmutableArray.CreateBuilder<WatchedDirectory>(ordered.Count);

        foreach (CanonicalPath directory in ordered)
        {
            if (recursive.Contains(directory))
            {
                plan.Add(new WatchedDirectory(directory, IncludeSubdirectories: true));
            }
            else if (!IsUnderAny(directory, recursive))
            {
                plan.Add(new WatchedDirectory(directory, IncludeSubdirectories: false));
            }
        }

        return plan.ToImmutable();
    }

    private static CanonicalPath NearestExisting(
        CanonicalPath directory,
        Func<CanonicalPath, bool> directoryExists)
    {
        for (CanonicalPath current = directory.Directory;
            !current.IsEmpty;
            current = current.Directory)
        {
            if (directoryExists(current))
            {
                return current;
            }
        }

        return CanonicalPath.None;
    }

    private static bool IsUnderAny(CanonicalPath directory, HashSet<CanonicalPath> roots)
    {
        foreach (CanonicalPath root in roots)
        {
            if (directory.StartsWith(root))
            {
                return true;
            }
        }

        return false;
    }
}
