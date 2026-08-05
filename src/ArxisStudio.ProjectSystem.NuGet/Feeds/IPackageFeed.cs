using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>What to look for.</summary>
public sealed record PackageSearchRequest
{
    /// <summary>Gets the text to search for. Empty asks for whatever the feed considers popular.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether prerelease packages may be returned.</summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>Gets how many results to skip, for paging.</summary>
    public int Skip
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Skip));

            field = value;
        }
    }

    /// <summary>Gets how many results to ask for.</summary>
    public int Take
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(Take));

            field = value;
        }
    } = 20;

    /// <summary>Returns what this asks for.</summary>
    /// <returns>Something like <c>"serilog" (20 from 0)</c>.</returns>
    public override string ToString() => $"\"{Query}\" ({Take} from {Skip})";
}

/// <summary>
/// A package a feed offered, described without any NuGet type.
/// </summary>
/// <remarks>
/// Everything here is optional except the identifier, because feeds differ in what they publish and
/// a missing description is not a broken package. The version strings are the feed's own spelling,
/// which is what a project file should end up holding.
/// </remarks>
public sealed record FoundPackage
{
    /// <summary>Gets the package identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version the feed considers current, when it said.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Gets the description, when the feed supplied one.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the authors, when the feed supplied any.</summary>
    public string? Authors { get; init; }

    /// <summary>Gets the project's own page, when the feed supplied one.</summary>
    public string? ProjectUrl { get; init; }

    /// <summary>Gets the licence, as an SPDX expression or a URL, when the feed supplied one.</summary>
    public string? License { get; init; }

    /// <summary>Gets how many times it has been downloaded, when the feed counted.</summary>
    public long? TotalDownloads { get; init; }

    /// <summary>Gets every version the feed listed, newest first.</summary>
    public ImmutableArray<string> Versions
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Returns the identifier and its current version.</summary>
    /// <returns>Something like <c>Serilog 4.1.0</c>.</returns>
    public override string ToString() => LatestVersion is null ? Id : $"{Id} {LatestVersion}";
}

/// <summary>
/// What a feed answered, and anything that went wrong asking.
/// </summary>
/// <remarks>
/// <para>
/// A feed being unreachable is an ordinary situation for a tool — an aeroplane, a proxy, a private
/// source that needs credentials — so it arrives as a diagnostic rather than an exception, in the
/// same shape as everything else this library reports.
/// </para>
/// <para>
/// The distinction this type exists to keep is between <em>nothing matched</em> and <em>nobody
/// answered</em>. Returning an empty list for both would let a tool tell somebody a package has no
/// versions when in fact the network is down, and the two call for opposite responses.
/// </para>
/// </remarks>
/// <typeparam name="T">What was asked for: a package, or a version.</typeparam>
public sealed class FeedResult<T>
{
    private FeedResult(ImmutableArray<T> items, ImmutableArray<ProjectDiagnostic> diagnostics)
    {
        Items = items;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets what the feed returned, in the order it returned them.</summary>
    public ImmutableArray<T> Items { get; }

    /// <summary>Gets anything worth saying about the request.</summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether the request failed rather than matched nothing.</summary>
    /// <remarks>
    /// Computed, like every other status in this library, so it cannot disagree with the evidence
    /// beside it. No items and no errors is a successful request: the query matched nothing.
    /// </remarks>
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

    /// <summary>Returns how many items came back, or that it failed.</summary>
    /// <returns>Something like <c>12 results</c>.</returns>
    public override string ToString() =>
        HasErrors ? "Failed" : $"{Items.Length} result{(Items.Length == 1 ? string.Empty : "s")}";

    internal static FeedResult<T> Create(
        ImmutableArray<T> items, ImmutableArray<ProjectDiagnostic> diagnostics) =>
        new(items, diagnostics);
}

/// <summary>Builds a <see cref="FeedResult{T}"/>.</summary>
/// <remarks>
/// Separate from the generic type so the factories can infer what they are building, which is what
/// turns <c>FeedResult&lt;FoundPackage&gt;.Found(packages)</c> into <c>FeedResult.Found(packages)</c>
/// at every call site.
/// </remarks>
public static class FeedResult
{
    /// <summary>Builds a result for a request the feed answered.</summary>
    /// <typeparam name="T">What was asked for.</typeparam>
    /// <param name="items">What it answered with, possibly none.</param>
    /// <returns>The result.</returns>
    public static FeedResult<T> Found<T>(ImmutableArray<T> items) =>
        FeedResult<T>.Create(items.IsDefault ? [] : items, []);

    /// <summary>Builds a result for a request that did not reach an answer.</summary>
    /// <typeparam name="T">What was asked for.</typeparam>
    /// <param name="diagnostic">Why not.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public static FeedResult<T> Failed<T>(ProjectDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return FeedResult<T>.Create([], [diagnostic]);
    }
}

/// <summary>
/// Somewhere packages can be looked up.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a class because feeds vary in ways this library should not try to
/// contain. A host with a private source, a credential provider, an offline mirror or an
/// organisation's own index implements this and everything above it keeps working.
/// </para>
/// <para>
/// It is also what keeps the tests honest: the contract forbids a test to reach a network, and a
/// fake feed is how installing "the latest version of something" is tested without one.
/// </para>
/// </remarks>
public interface IPackageFeed
{
    /// <summary>Gets a name for the feed, for diagnostics and for a user interface to show.</summary>
    string Name { get; }

    /// <summary>Finds packages matching a query.</summary>
    /// <param name="request">What to look for.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What was found, or why nothing was.</returns>
    ValueTask<FeedResult<FoundPackage>> SearchAsync(
        PackageSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every version of one package, newest first.
    /// </summary>
    /// <remarks>
    /// Separate from searching because it answers a different question and a feed answers it from a
    /// different place: searching ranks packages, and this enumerates one package's history, which
    /// is what an update needs.
    /// </remarks>
    /// <param name="packageId">The package to look up.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>
    /// Its versions, newest first, and empty when the feed does not have the package — which is not
    /// the same answer as a feed that could not be reached, and the result says which.
    /// </returns>
    ValueTask<FeedResult<string>> GetVersionsAsync(
        string packageId, CancellationToken cancellationToken);
}
