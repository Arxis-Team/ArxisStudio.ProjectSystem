using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// A NuGet V3 feed reached over HTTP, such as nuget.org.
/// </summary>
/// <remarks>
/// <para>
/// Enough of the V3 protocol to search a public feed and list a package's versions: read the
/// service index, find the search and package-base addresses it advertises, and ask them. That is
/// the whole of it, and it is a documented and stable part of the protocol.
/// </para>
/// <para>
/// <b>It does not read <c>NuGet.config</c> and does not authenticate.</b> Discovering configured
/// sources is hierarchical, and private feeds need NuGet's credential-provider model — neither is
/// something to half-implement, because a half-implementation fails by silently not finding a
/// package rather than by saying so. A host that has private sources implements
/// <see cref="IPackageFeed"/> over its own client instead; that seam is why the interface exists.
/// </para>
/// <para>
/// The <see cref="HttpClient"/> is supplied and is not disposed here. Its lifetime belongs to
/// whoever created it, which is the shape that lets a host configure a proxy, a handler, a timeout
/// and a user agent once for everything it does.
/// </para>
/// </remarks>
public sealed class NuGetHttpFeed : IPackageFeed, IDisposable
{
    /// <summary>The public feed, for a host that has not been told to use another.</summary>
    public static readonly Uri NuGetOrg = new("https://api.nuget.org/v3/index.json");

    private const string SearchResource = "SearchQueryService";
    private const string PackageBaseResource = "PackageBaseAddress/3.0.0";

    private readonly HttpClient _client;
    private readonly Uri _serviceIndex;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    private string? _searchAddress;
    private string? _packageBaseAddress;

    /// <summary>Creates a feed.</summary>
    /// <param name="client">The client to use. Not disposed by this object.</param>
    /// <param name="serviceIndex">The feed's service index, or <see langword="null"/> for nuget.org.</param>
    /// <param name="name">A name for diagnostics, or <see langword="null"/> to use the host name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public NuGetHttpFeed(HttpClient client, Uri? serviceIndex = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _serviceIndex = serviceIndex ?? NuGetOrg;
        Name = name ?? _serviceIndex.Host;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Releases the gate that serialises reading the service index.</summary>
    /// <remarks>
    /// Not the <see cref="HttpClient"/>: that was handed in, and disposing something a caller still
    /// holds is how a shared client becomes unusable halfway through a session.
    /// </remarks>
    public void Dispose() => _indexGate.Dispose();

    /// <inheritdoc />
    public async ValueTask<FeedResult<FoundPackage>> SearchAsync(
        PackageSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (await ResolveAsync(SearchResource, cancellationToken).ConfigureAwait(false) is not { } search)
            {
                return FeedResult.Failed<FoundPackage>(Diagnostic(
                    PackageDiagnosticCodes.FeedNotUnderstood,
                    $"'{Name}' advertises no search service."));
            }

            string url = string.Create(
                CultureInfo.InvariantCulture,
                $"{search}?q={Uri.EscapeDataString(request.Query)}&skip={request.Skip}&take={request.Take}" +
                $"&prerelease={(request.IncludePrerelease ? "true" : "false")}&semVerLevel=2.0.0");

            string json = await GetAsync(url, cancellationToken).ConfigureAwait(false);

            return FeedResult.Found(FeedResponseReader.ReadSearch(json));
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return FeedResult.Failed<FoundPackage>(Unreachable(exception));
        }
        catch (JsonException exception)
        {
            return FeedResult.Failed<FoundPackage>(Diagnostic(
                PackageDiagnosticCodes.FeedNotUnderstood,
                $"'{Name}' answered with something that is not a search response: {exception.Message}"));
        }
    }

    /// <inheritdoc />
    public async ValueTask<FeedResult<string>> GetVersionsAsync(
        string packageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        try
        {
            if (await ResolveAsync(PackageBaseResource, cancellationToken).ConfigureAwait(false) is not { } packages)
            {
                return FeedResult.Failed<string>(Diagnostic(
                    PackageDiagnosticCodes.FeedNotUnderstood,
                    $"'{Name}' advertises no package base address."));
            }

            // The protocol requires the identifier lowercased, and the invariant casing rather than
            // the current culture: a Turkish machine lowercases 'I' to a dotless one, which is a
            // different URL and a package that cannot be found on that machine alone.
            string url = packages.TrimEnd('/') + "/" + packageId.ToLowerInvariant() + "/index.json";

            using HttpResponseMessage response = await _client
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);

            // A feed says "no such package" with a 404, which is an answer rather than a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FeedResult.Found(ImmutableArray<string>.Empty);
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return FeedResult.Found(FeedResponseReader.ReadVersions(json));
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return FeedResult.Failed<string>(Unreachable(exception));
        }
        catch (JsonException exception)
        {
            return FeedResult.Failed<string>(Diagnostic(
                PackageDiagnosticCodes.FeedNotUnderstood,
                $"'{Name}' answered with something that is not a version index: {exception.Message}"));
        }
    }

    /// <summary>
    /// Finds an address the feed advertises, reading the service index once.
    /// </summary>
    /// <remarks>
    /// Cached because a search and a version lookup both need it and the index does not change while
    /// a tool is running. The gate is there because a user interface will happily issue several
    /// searches at once, and fetching the same index three times to arrive at the same answer is
    /// three round trips nobody asked for.
    /// </remarks>
    private async ValueTask<string?> ResolveAsync(string resourceType, CancellationToken cancellationToken)
    {
        if (Cached(resourceType) is { } known)
        {
            return known;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Cached(resourceType) is { } raced)
            {
                return raced;
            }

            string json = await GetAsync(_serviceIndex.ToString(), cancellationToken).ConfigureAwait(false);

            _searchAddress = FeedResponseReader.ReadResource(json, SearchResource);
            _packageBaseAddress = FeedResponseReader.ReadResource(json, PackageBaseResource);

            return Cached(resourceType);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private string? Cached(string resourceType) =>
        string.Equals(resourceType, SearchResource, StringComparison.Ordinal)
            ? _searchAddress
            : _packageBaseAddress;

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a failure is the network rather than a mistake.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskCanceledException"/> is in the list because that is what
    /// <see cref="HttpClient"/> raises on its own timeout, which is a transport failure wearing a
    /// cancellation's clothes. A cancellation the caller actually asked for is filtered out first,
    /// so it still propagates as the contract requires.
    /// </remarks>
    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException
            || (exception is TaskCanceledException canceled && canceled.InnerException is TimeoutException);

    private ProjectDiagnostic Unreachable(Exception exception) => Diagnostic(
        PackageDiagnosticCodes.FeedUnreachable, $"'{Name}' could not be reached: {exception.Message}");

    private static ProjectDiagnostic Diagnostic(string code, string message) =>
        new(code, message, ProjectDiagnosticSeverity.Error) { ProviderName = "NuGet" };
}
