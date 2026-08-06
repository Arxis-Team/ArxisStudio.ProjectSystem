using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    /// <summary>
    /// The registration resource, named with its version because the version is the whole point.
    /// </summary>
    /// <remarks>
    /// 3.6.0 is the SemVer 2.0.0 registration, and it is the only one carrying <c>deprecation</c>
    /// and <c>vulnerabilities</c>. Matching the unversioned <c>RegistrationsBaseUrl</c> instead --
    /// which every feed advertises, and which ReadResource would happily accept -- would return a
    /// document with those fields simply absent, and a package with 28 published advisories would
    /// read as a package with none.
    /// </remarks>
    private const string RegistrationResource = "RegistrationsBaseUrl/3.6.0";

    private readonly HttpClient _client;
    private readonly Uri _serviceIndex;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    private string? _searchAddress;
    private string? _packageBaseAddress;
    private string? _registrationAddress;

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

            string json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

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

    /// <inheritdoc />
    public async ValueTask<FeedResult<PackageVersionMetadata>> GetMetadataAsync(
        string packageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        try
        {
            if (await ResolveAsync(RegistrationResource, cancellationToken).ConfigureAwait(false)
                is not { } registrations)
            {
                // Named precisely, because the near miss is the likely one: a feed advertising only
                // the unversioned RegistrationsBaseUrl publishes neither deprecation nor
                // vulnerabilities, so reading it would answer "no advisories" for a package that has
                // them. Measured on nuget.org, where the same package shows 32 deprecated versions
                // and 28 advisories under 3.6.0 and none at all under the base resource.
                return FeedResult.Failed<PackageVersionMetadata>(Diagnostic(
                    PackageDiagnosticCodes.FeedNotUnderstood,
                    $"'{Name}' advertises no {RegistrationResource}, which is the only registration " +
                    "resource carrying deprecation and vulnerability data."));
            }

            string url = registrations.TrimEnd('/') + "/" + packageId.ToLowerInvariant() + "/index.json";

            using HttpResponseMessage response = await _client
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FeedResult.Found(ImmutableArray<PackageVersionMetadata>.Empty);
            }

            response.EnsureSuccessStatusCode();

            string json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

            (ImmutableArray<PackageVersionMetadata> inlined, ImmutableArray<string> pages) =
                FeedResponseReader.ReadRegistrationIndex(json);

            var all = ImmutableArray.CreateBuilder<PackageVersionMetadata>();

            all.AddRange(inlined);

            // Sequentially rather than all at once. A registration is read while somebody waits, but
            // a package with a long history has enough pages that firing them together is a burst a
            // feed may rate-limit -- and this library does not own the HttpClient's limits.
            foreach (string page in pages)
            {
                all.AddRange(FeedResponseReader.ReadRegistrationPage(
                    await GetAsync(page, cancellationToken).ConfigureAwait(false)));
            }

            return FeedResult.Found(Newest(all));
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return FeedResult.Failed<PackageVersionMetadata>(Unreachable(exception));
        }
        catch (JsonException exception)
        {
            return FeedResult.Failed<PackageVersionMetadata>(Diagnostic(
                PackageDiagnosticCodes.FeedNotUnderstood,
                $"'{Name}' answered with something that is not a registration: {exception.Message}"));
        }
    }

    /// <summary>
    /// Newest first, matching <see cref="GetVersionsAsync"/>, and ordered by the same comparison.
    /// </summary>
    /// <remarks>
    /// A registration is ascending and pages arrive in the feed's order, so sorting here is what
    /// makes the two methods agree — a caller pairing a version list with this must not have to
    /// wonder whether the orders match.
    /// </remarks>
    private static ImmutableArray<PackageVersionMetadata> Newest(
        ImmutableArray<PackageVersionMetadata>.Builder read)
    {
        var byVersion = new Dictionary<string, PackageVersionMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (PackageVersionMetadata metadata in read)
        {
            byVersion[metadata.Version] = metadata;
        }

        return
        [
            .. PackageVersions
                .Sort(byVersion.Keys)
                .Select(version => byVersion[version]),
        ];
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
            _registrationAddress = FeedResponseReader.ReadResource(json, RegistrationResource);

            return Cached(resourceType);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private string? Cached(string resourceType) => resourceType switch
    {
        SearchResource => _searchAddress,
        PackageBaseResource => _packageBaseAddress,
        _ => _registrationAddress,
    };

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a response body, decompressing it if the client has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// nuget.org's registration blobs are stored gzipped and served that way <em>whatever the
    /// client asked for</em> — measured: the response carries <c>Content-Encoding: gzip</c> even for
    /// a request sending an empty <c>Accept-Encoding</c>. A default
    /// <see cref="HttpClient"/> decompresses nothing, because
    /// <see cref="System.Net.Http.HttpClientHandler.AutomaticDecompression"/> is
    /// <see cref="DecompressionMethods.None"/> unless a host says otherwise, so the first byte to
    /// arrive is <c>0x1F</c> and the parse fails on a document that is perfectly valid underneath.
    /// </para>
    /// <para>
    /// Handled here rather than by asking hosts to configure their client, because this library
    /// deliberately does not own it and a method that only works against a specially prepared
    /// <see cref="HttpClient"/> is a method that fails for whoever writes the obvious code. The
    /// header is the test and it is exact: when automatic decompression did the work, .NET removes
    /// <c>Content-Encoding</c> from the response, so a header that is still there means the bytes
    /// are still compressed.
    /// </para>
    /// </remarks>
    private static async Task<string> ReadAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Stream body = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (body.ConfigureAwait(false))
        {
            if (!response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
            {
                using var plain = new StreamReader(body);

                return await plain.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var decompressed = new GZipStream(body, CompressionMode.Decompress);

            await using (decompressed.ConfigureAwait(false))
            {
                using var reader = new StreamReader(decompressed);

                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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
