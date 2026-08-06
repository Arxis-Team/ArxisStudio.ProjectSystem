using System.Collections.Immutable;
using System.Linq;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>How bad an advisory says a vulnerability is.</summary>
/// <remarks>
/// A feed writes these as the numbers <c>"0"</c> to <c>"3"</c>. They are named here because a
/// number in a user interface is a number somebody has to look up, and the mapping is a documented
/// part of the protocol rather than a guess.
/// </remarks>
public enum PackageVulnerabilitySeverity
{
    /// <summary>The feed reported a severity this library does not recognise.</summary>
    /// <remarks>
    /// Not the same as the feed saying nothing, which is an absent severity. A severity that has
    /// been added since this was written must not be silently reported as the least serious one.
    /// </remarks>
    Unknown = -1,

    /// <summary>Low.</summary>
    Low = 0,

    /// <summary>Moderate.</summary>
    Moderate = 1,

    /// <summary>High.</summary>
    High = 2,

    /// <summary>Critical.</summary>
    Critical = 3,
}

/// <summary>One published advisory against one version of a package.</summary>
public sealed record PackageVulnerability
{
    /// <summary>Gets where the advisory is published, when the feed said.</summary>
    public string? AdvisoryUrl { get; init; }

    /// <summary>Gets how serious it is, or <see langword="null"/> when the feed did not say.</summary>
    public PackageVulnerabilitySeverity? Severity { get; init; }

    /// <summary>Returns the severity and the advisory.</summary>
    /// <returns>Something like <c>High: https://github.com/advisories/GHSA-…</c>.</returns>
    public override string ToString() =>
        $"{Severity?.ToString() ?? "Unrated"}{(AdvisoryUrl is null ? string.Empty : ": " + AdvisoryUrl)}";
}

/// <summary>
/// A package version its author has withdrawn, and what to use instead.
/// </summary>
/// <remarks>
/// Deprecation is per version rather than per package: an author usually deprecates the versions
/// that exist and leaves the identifier free to be published again.
/// </remarks>
public sealed record PackageDeprecation
{
    /// <summary>Gets what the author said about it, when they said anything.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the reasons the feed gave, as it spelled them — <c>Legacy</c>, <c>CriticalBugs</c>,
    /// <c>Other</c>.
    /// </summary>
    /// <remarks>
    /// Text rather than an enumeration, because unlike a severity these are already words and a feed
    /// adding one should read as a new reason rather than as no reason.
    /// </remarks>
    public ImmutableArray<string> Reasons
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the package the author points at instead, when they named one.</summary>
    public string? AlternatePackageId { get; init; }

    /// <summary>
    /// Gets the versions of that package the author points at, as written, when they said.
    /// </summary>
    /// <remarks>
    /// A NuGet version range, uninterpreted, for the reason ranges are uninterpreted everywhere in
    /// this family: <c>*</c> and <c>[2.0,)</c> are both meaningful and resolving them is NuGet's job.
    /// </remarks>
    public string? AlternateVersionRange { get; init; }

    /// <summary>Returns the reasons and the alternative.</summary>
    /// <returns>Something like <c>Legacy, use Azure.Security.KeyVault.Certificates</c>.</returns>
    public override string ToString()
    {
        string reasons = Reasons.IsEmpty ? "Deprecated" : string.Join(", ", Reasons);

        return AlternatePackageId is null ? reasons : $"{reasons}, use {AlternatePackageId}";
    }
}

/// <summary>
/// What a feed publishes about one version of a package.
/// </summary>
/// <remarks>
/// <para>
/// One of these per version, because every field below can differ between versions of the same
/// package: an advisory is against the versions that carry the bug, a deprecation is applied to the
/// versions that exist when the author gives up on it, and a licence can change.
/// </para>
/// <para>
/// Everything is optional except the version. Feeds differ in what they publish, and a package with
/// no licence recorded is not a broken package.
/// </para>
/// </remarks>
public sealed record PackageVersionMetadata
{
    /// <summary>Gets the version, as the feed spelled it.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets a value indicating whether the feed still offers this version in listings.
    /// </summary>
    /// <remarks>
    /// An unlisted version is hidden from search but still restorable, which is how an author
    /// retires a version without breaking the projects that pinned it.
    /// </remarks>
    public bool IsListed { get; init; } = true;

    /// <summary>Gets the licence as an SPDX expression, when the feed supplied one.</summary>
    public string? LicenseExpression { get; init; }

    /// <summary>Gets the licence's URL, when the feed supplied one.</summary>
    public string? LicenseUrl { get; init; }

    /// <summary>Gets the project's own page, when the feed supplied one.</summary>
    public string? ProjectUrl { get; init; }

    /// <summary>Gets the description, when the feed supplied one.</summary>
    public string? Description { get; init; }

    /// <summary>Gets whether this version has been deprecated, and what to use instead.</summary>
    public PackageDeprecation? Deprecation { get; init; }

    /// <summary>Gets the advisories published against this version.</summary>
    public ImmutableArray<PackageVulnerability> Vulnerabilities
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets a value indicating whether anything here argues against using this version.</summary>
    public bool HasWarnings => Deprecation is not null || !Vulnerabilities.IsEmpty;

    /// <summary>Returns the version and whatever argues against it.</summary>
    /// <returns>Something like <c>2.2.0 (deprecated, 4 advisories)</c>.</returns>
    public override string ToString()
    {
        if (!HasWarnings)
        {
            return Version;
        }

        string deprecated = Deprecation is null ? string.Empty : "deprecated";
        string advisories = Vulnerabilities.IsEmpty
            ? string.Empty
            : $"{Vulnerabilities.Length} advisor{(Vulnerabilities.Length == 1 ? "y" : "ies")}";

        return $"{Version} ({string.Join(", ", new[] { deprecated, advisories }.Where(
            static part => part.Length > 0))})";
    }
}
