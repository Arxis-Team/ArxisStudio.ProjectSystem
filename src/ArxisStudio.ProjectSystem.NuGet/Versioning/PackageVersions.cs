using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NuGet.Versioning;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Ordering and choosing among the versions of a package.
/// </summary>
/// <remarks>
/// <para>
/// Versions cross this boundary as the strings a project file holds, never as a NuGet type. That is
/// the same rule every package in this family follows and an architecture test enforces it: a
/// consumer inspecting or choosing a version does not put NuGet on their compile line.
/// </para>
/// <para>
/// Inside, the comparison is NuGet's own. SemVer 2 ordering has a fourth numeric field, a prerelease
/// that sorts <em>before</em> the release it precedes, dot-separated identifiers that compare
/// numerically or lexically depending on their shape, and build metadata that is ignored entirely.
/// Reimplementing that would be wrong in ways nothing would notice until a tool offered somebody a
/// downgrade.
/// </para>
/// </remarks>
public static class PackageVersions
{
    /// <summary>Whether a version is a prerelease, such as <c>1.0.0-beta.2</c>.</summary>
    /// <param name="version">The version to read.</param>
    /// <returns>
    /// <see langword="true"/> when it carries a prerelease label. An unreadable version is not a
    /// prerelease, because it is not a version.
    /// </returns>
    public static bool IsPrerelease(string? version) =>
        NuGetVersion.TryParse(version, out NuGetVersion? parsed) && parsed.IsPrerelease;

    /// <summary>Whether a string is a version this library can order.</summary>
    /// <param name="version">The string to test.</param>
    /// <returns><see langword="true"/> when it parses.</returns>
    public static bool IsValid(string? version) => NuGetVersion.TryParse(version, out _);

    /// <summary>
    /// Compares two versions the way NuGet does.
    /// </summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>Negative, zero or positive, as <see cref="IComparable{T}.CompareTo"/> reports.</returns>
    /// <exception cref="ArgumentException">Either string is not a version.</exception>
    public static int Compare(string left, string right)
    {
        if (!NuGetVersion.TryParse(left, out NuGetVersion? first))
        {
            throw new ArgumentException($"'{left}' is not a version.", nameof(left));
        }

        if (!NuGetVersion.TryParse(right, out NuGetVersion? second))
        {
            throw new ArgumentException($"'{right}' is not a version.", nameof(right));
        }

        return first.CompareTo(second);
    }

    /// <summary>
    /// Orders versions, newest first by default, dropping duplicates.
    /// </summary>
    /// <remarks>
    /// Strings that are not versions are left out rather than throwing. A feed that offers one has
    /// offered something no project could reference, and refusing the entire list because of it
    /// would turn one bad entry in a package's history into an unusable package.
    /// </remarks>
    /// <param name="versions">The versions to order.</param>
    /// <param name="newestFirst">Whether to put the newest first.</param>
    /// <returns>The versions, ordered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="versions"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<string> Sort(IEnumerable<string?> versions, bool newestFirst = true)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var parsed = new List<NuGetVersion>();
        var seen = new HashSet<NuGetVersion>();

        foreach (string? version in versions)
        {
            if (NuGetVersion.TryParse(version, out NuGetVersion? one) && seen.Add(one))
            {
                parsed.Add(one);
            }
        }

        parsed.Sort();

        if (newestFirst)
        {
            parsed.Reverse();
        }

        // The original spelling, not NuGet's normalisation of it: a project that says 1.0 means 1.0,
        // and rewriting it to 1.0.0 would be a change nobody asked for.
        return [.. parsed.ConvertAll(static version => version.OriginalVersion ?? version.ToNormalizedString())];
    }

    /// <summary>
    /// Picks the newest version, optionally allowing a prerelease.
    /// </summary>
    /// <remarks>
    /// Excluding prereleases is the default because that is what a person means by "the latest
    /// version" and what installing without saying otherwise should do. A package whose only
    /// versions are prereleases returns <see langword="null"/> rather than quietly offering one.
    /// </remarks>
    /// <param name="versions">The versions to choose from.</param>
    /// <param name="includePrerelease">Whether a prerelease may be chosen.</param>
    /// <returns>The newest, or <see langword="null"/> when nothing qualifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="versions"/> is <see langword="null"/>.</exception>
    public static string? Latest(IEnumerable<string?> versions, bool includePrerelease = false)
    {
        ArgumentNullException.ThrowIfNull(versions);

        NuGetVersion? best = null;

        foreach (string? version in versions)
        {
            if (!NuGetVersion.TryParse(version, out NuGetVersion? one))
            {
                continue;
            }

            if (one.IsPrerelease && !includePrerelease)
            {
                continue;
            }

            if (best is null || one.CompareTo(best) > 0)
            {
                best = one;
            }
        }

        return best?.OriginalVersion ?? best?.ToNormalizedString();
    }
}
