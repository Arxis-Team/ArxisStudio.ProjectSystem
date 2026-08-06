using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.NuGet.Tests;

/// <summary>
/// The V3 feed client, answered by a handler rather than by a network.
/// </summary>
/// <remarks>
/// The contract forbids a test to reach a network, and it would be the wrong thing here anyway:
/// asserting that nuget.org returns particular packages tests nuget.org. What is worth testing is
/// the protocol — which addresses are read from the service index, what a 404 means, and what
/// happens when nobody answers — and a stub handler makes every one of those exact.
/// </remarks>
public sealed class NuGetHttpFeedTests
{
    private const string ServiceIndex = """
        {
          "version": "3.0.0",
          "resources": [
            { "@id": "https://example.test/registration/", "@type": "RegistrationsBaseUrl/3.6.0" },
            { "@id": "https://example.test/query", "@type": "SearchQueryService/3.5.0" },
            { "@id": "https://example.test/flat/", "@type": "PackageBaseAddress/3.0.0" }
          ]
        }
        """;

    private const string SearchResponse = """
        {
          "totalHits": 1,
          "data": [
            {
              "id": "Serilog",
              "version": "4.1.0",
              "description": "Simple .NET logging",
              "authors": ["Serilog Contributors", "Nicholas Blumhardt"],
              "projectUrl": "https://serilog.net",
              "licenseExpression": "Apache-2.0",
              "totalDownloads": 1234567890,
              "versions": [
                { "version": "3.0.0", "downloads": 10 },
                { "version": "4.1.0", "downloads": 20 },
                { "version": "5.0.0-rc.1", "downloads": 1 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Searching_ReadsTheAddressFromTheServiceIndex()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/query?q=serilog&skip=0&take=20&prerelease=false&semVerLevel=2.0.0"] = SearchResponse,
        };

        FeedResult<FoundPackage> result = await Search(handler, new PackageSearchRequest { Query = "serilog" });

        Assert.False(result.HasErrors);

        FoundPackage package = Assert.Single(result.Items);

        Assert.Equal("Serilog", package.Id);
        Assert.Equal("4.1.0", package.LatestVersion);
        Assert.Equal("Simple .NET logging", package.Description);
        Assert.Equal("Serilog Contributors, Nicholas Blumhardt", package.Authors);
        Assert.Equal("https://serilog.net", package.ProjectUrl);
        Assert.Equal("Apache-2.0", package.License);
        Assert.Equal(1234567890, package.TotalDownloads);

        // Newest first, whatever order the feed listed them in.
        Assert.Equal(["5.0.0-rc.1", "4.1.0", "3.0.0"], package.Versions);
    }

    [Fact]
    public async Task TheQuery_IsEscaped()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/query?q=a%20b%26c&skip=5&take=3&prerelease=true&semVerLevel=2.0.0"] =
                """{ "data": [] }""",
        };

        FeedResult<FoundPackage> result = await Search(
            handler,
            new PackageSearchRequest { Query = "a b&c", Skip = 5, Take = 3, IncludePrerelease = true });

        Assert.False(result.HasErrors);
        Assert.Empty(result.Items);
    }

    /// <summary>The index is read once, however many questions are asked of the feed.</summary>
    [Fact]
    public async Task TheServiceIndex_IsFetchedOnce()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/query?q=&skip=0&take=20&prerelease=false&semVerLevel=2.0.0"] = """{ "data": [] }""",
            ["https://example.test/flat/serilog/index.json"] = """{ "versions": ["1.0.0"] }""",
        };

        using var client = new HttpClient(handler);
        using var feed = new NuGetHttpFeed(client);

        await feed.SearchAsync(new PackageSearchRequest(), TestContext.Current.CancellationToken);
        await feed.SearchAsync(new PackageSearchRequest(), TestContext.Current.CancellationToken);
        await feed.GetVersionsAsync("Serilog", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CountOf("https://api.nuget.org/v3/index.json"));
    }

    [Fact]
    public async Task Versions_ComeBackNewestFirst()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/flat/serilog/index.json"] =
                """{ "versions": ["1.0.0", "1.10.0", "1.2.0", "2.0.0-rc.1"] }""",
        };

        FeedResult<string> result = await Versions(handler, "Serilog");

        Assert.False(result.HasErrors);
        Assert.Equal(["2.0.0-rc.1", "1.10.0", "1.2.0", "1.0.0"], result.Items);
    }

    /// <summary>
    /// The identifier is lowercased invariantly, not by the current culture: a Turkish machine
    /// lowercases <c>I</c> to a dotless one, which is a different URL and a package that cannot be
    /// found on that machine alone.
    /// </summary>
    [Fact]
    public async Task ThePackageIdentifier_IsLowercasedForTheUrl()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/flat/microsoft.identity.client/index.json"] = """{ "versions": ["1.0.0"] }""",
        };

        FeedResult<string> result = await Versions(handler, "Microsoft.Identity.Client");

        Assert.False(result.HasErrors);
        Assert.Equal(["1.0.0"], result.Items);
    }

    /// <summary>
    /// The distinction the result type exists for. A package the feed does not have is an answer;
    /// a feed nobody could reach is not, and telling somebody a package has no versions when the
    /// network is down would be a lie in the shape of a fact.
    /// </summary>
    [Fact]
    public async Task APackageTheFeedDoesNotHave_IsAnEmptyAnswerRatherThanAFailure()
    {
        var handler = new StubHandler { ["https://api.nuget.org/v3/index.json"] = ServiceIndex };

        FeedResult<string> result = await Versions(handler, "Nothing.Like.This");

        Assert.False(result.HasErrors);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task AFeedThatCannotBeReached_IsADiagnostic()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("no route to host") };

        FeedResult<FoundPackage> result = await Search(handler, new PackageSearchRequest());

        Assert.True(result.HasErrors);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(PackageDiagnosticCodes.FeedUnreachable, diagnostic.Code);
        Assert.Contains("no route to host", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("NuGet", diagnostic.ProviderName);
    }

    [Fact]
    public async Task AFeedThatAdvertisesNoSearchService_IsADiagnostic()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = """{ "resources": [] }""",
        };

        FeedResult<FoundPackage> result = await Search(handler, new PackageSearchRequest());

        Assert.Equal(PackageDiagnosticCodes.FeedNotUnderstood, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task AFeedThatAnswersWithNonsense_IsADiagnostic()
    {
        var handler = new StubHandler
        {
            ["https://api.nuget.org/v3/index.json"] = ServiceIndex,
            ["https://example.test/query?q=&skip=0&take=20&prerelease=false&semVerLevel=2.0.0"] = "not json at all",
        };

        FeedResult<FoundPackage> result = await Search(handler, new PackageSearchRequest());

        Assert.Equal(PackageDiagnosticCodes.FeedNotUnderstood, Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>A timeout arrives as a cancellation, and is the transport failing rather than the caller asking.</summary>
    [Fact]
    public async Task AFeedThatTimesOut_IsADiagnostic()
    {
        var handler = new StubHandler
        {
            Throw = new TaskCanceledException("timed out", new TimeoutException()),
        };

        FeedResult<FoundPackage> result = await Search(handler, new PackageSearchRequest());

        Assert.Equal(PackageDiagnosticCodes.FeedUnreachable, Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>And a cancellation the caller did ask for still propagates, as the contract requires.</summary>
    [Fact]
    public async Task ACancelledSearch_Throws()
    {
        var handler = new StubHandler { ["https://api.nuget.org/v3/index.json"] = ServiceIndex };

        using var client = new HttpClient(handler);
        using var feed = new NuGetHttpFeed(client);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await feed.SearchAsync(new PackageSearchRequest(), cancellation.Token));
    }

    [Fact]
    public void ItsNameDefaultsToTheHost()
    {
        using var client = new HttpClient(new StubHandler());

        using (var feed = new NuGetHttpFeed(client))
        {
            Assert.Equal("api.nuget.org", feed.Name);
        }

        using (var named = new NuGetHttpFeed(client, new Uri("https://example.test/index.json"), "Internal"))
        {
            Assert.Equal("Internal", named.Name);
        }
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new NuGetHttpFeed(null!));

        using var client = new HttpClient(new StubHandler());
        using var feed = new NuGetHttpFeed(client);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await feed.SearchAsync(null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await feed.GetVersionsAsync("  ", CancellationToken.None));
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 0)]
    public void ANonsensicalPage_Throws(int skip, int take) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageSearchRequest { Skip = skip, Take = take });

    /// <summary>
    /// A registration index with its leaves inlined, which is how a feed answers for a package with
    /// few versions.
    /// </summary>
    /// <remarks>
    /// The shapes are copied from what nuget.org actually answers, including the details that look
    /// like mistakes: a severity is a <em>number written as a string</em>, and a deprecation's
    /// alternate package carries a version range of its own.
    /// </remarks>
    private const string Registration = """
        {
          "count": 1,
          "items": [
            {
              "count": 4,
              "items": [
                {
                  "catalogEntry": {
                    "id": "Sample",
                    "version": "1.0.0",
                    "licenseUrl": "https://example.test/licence",
                    "listed": false
                  }
                },
                {
                  "catalogEntry": {
                    "id": "Sample",
                    "version": "2.0.0",
                    "licenseExpression": "MIT",
                    "projectUrl": "https://example.test",
                    "description": "A sample",
                    "deprecation": {
                      "message": "Use the other one.",
                      "reasons": [ "Legacy", "Other" ],
                      "alternatePackage": { "id": "Sample.Next", "range": "[2.0,)" }
                    }
                  }
                },
                {
                  "catalogEntry": {
                    "id": "Sample",
                    "version": "3.0.0",
                    "vulnerabilities": [
                      { "advisoryUrl": "https://example.test/one", "severity": "3" },
                      { "advisoryUrl": "https://example.test/two", "severity": "9" },
                      { "advisoryUrl": "https://example.test/three" }
                    ]
                  }
                },
                {
                  "catalogEntry": { "id": "Sample", "version": "4.0.0" }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Metadata_ReadsADeprecationAndWhatItPointsAt()
    {
        PackageVersionMetadata deprecated = At(await Metadata(Feed()), "2.0.0");

        Assert.NotNull(deprecated.Deprecation);
        Assert.Equal("Use the other one.", deprecated.Deprecation.Message);
        Assert.Equal(["Legacy", "Other"], deprecated.Deprecation.Reasons);
        Assert.Equal("Sample.Next", deprecated.Deprecation.AlternatePackageId);
        Assert.Equal("[2.0,)", deprecated.Deprecation.AlternateVersionRange);
        Assert.True(deprecated.HasWarnings);
    }

    /// <summary>
    /// The severity arrives as a number spelled as a string, and naming it is the whole reason this
    /// is read rather than passed through.
    /// </summary>
    [Fact]
    public async Task Metadata_NamesTheSeverityOfEachAdvisory()
    {
        PackageVersionMetadata vulnerable = At(await Metadata(Feed()), "3.0.0");

        Assert.Equal(3, vulnerable.Vulnerabilities.Length);
        Assert.Equal(PackageVulnerabilitySeverity.Critical, vulnerable.Vulnerabilities[0].Severity);
        Assert.Equal("https://example.test/one", vulnerable.Vulnerabilities[0].AdvisoryUrl);
        Assert.True(vulnerable.HasWarnings);
    }

    /// <summary>
    /// A severity added after this was written must not be reported as the least serious one, and a
    /// feed that omits it must not be reported as having rated it at all.
    /// </summary>
    [Fact]
    public async Task Metadata_OfAnUnrecognisedOrAbsentSeverity_DoesNotInventALowOne()
    {
        ImmutableArray<PackageVulnerability> found = At(await Metadata(Feed()), "3.0.0").Vulnerabilities;

        Assert.Equal(PackageVulnerabilitySeverity.Unknown, found[1].Severity);
        Assert.Null(found[2].Severity);
    }

    [Fact]
    public async Task Metadata_ReadsTheLicenceInEitherSpelling()
    {
        ImmutableArray<PackageVersionMetadata> all = (await Metadata(Feed())).Items;

        Assert.Equal("MIT", At(all, "2.0.0").LicenseExpression);
        Assert.Equal("https://example.test/licence", At(all, "1.0.0").LicenseUrl);
    }

    /// <summary>
    /// A feed writes <c>listed</c> only to say no. Defaulting an absent one to unlisted would hide
    /// every version of every feed that omits it.
    /// </summary>
    [Fact]
    public async Task Metadata_TreatsAnAbsentListedFlagAsListed()
    {
        ImmutableArray<PackageVersionMetadata> all = (await Metadata(Feed())).Items;

        Assert.False(At(all, "1.0.0").IsListed);
        Assert.True(At(all, "4.0.0").IsListed);
    }

    [Fact]
    public async Task Metadata_OfAVersionWithNothingAgainstIt_HasNoWarnings()
    {
        PackageVersionMetadata clean = At(await Metadata(Feed()), "4.0.0");

        Assert.Null(clean.Deprecation);
        Assert.Empty(clean.Vulnerabilities);
        Assert.False(clean.HasWarnings);
    }

    /// <summary>Newest first, so that this and <c>GetVersionsAsync</c> agree without being paired.</summary>
    [Fact]
    public async Task Metadata_IsNewestFirst()
    {
        Assert.Equal(
            ["4.0.0", "3.0.0", "2.0.0", "1.0.0"],
            (await Metadata(Feed())).Items.Select(static m => m.Version));
    }

    /// <summary>
    /// A feed inlines the leaves for a small package and pages them for a large one — measured on
    /// nuget.org, where one package arrives inlined and another arrives as three pages carrying
    /// nothing but their addresses. A reader that only looked at what was inlined would answer
    /// "no advisories" for the second, which is the worst answer available.
    /// </summary>
    [Fact]
    public async Task Metadata_FollowsThePagesWhenTheIndexDoesNotCarryTheLeaves()
    {
        var handler = new StubHandler
        {
            ["https://example.test/index.json"] = ServiceIndex,
            ["https://example.test/registration/sample/index.json"] = """
                {
                  "count": 2,
                  "items": [
                    { "@id": "https://example.test/registration/sample/page/1.json", "lower": "1.0.0", "upper": "1.0.0" },
                    { "@id": "https://example.test/registration/sample/page/2.json", "lower": "2.0.0", "upper": "2.0.0" }
                  ]
                }
                """,
            ["https://example.test/registration/sample/page/1.json"] = """
                { "items": [ { "catalogEntry": { "id": "Sample", "version": "1.0.0" } } ] }
                """,
            ["https://example.test/registration/sample/page/2.json"] = """
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "Sample",
                        "version": "2.0.0",
                        "vulnerabilities": [ { "advisoryUrl": "https://example.test/a", "severity": "2" } ]
                      }
                    }
                  ]
                }
                """,
        };

        FeedResult<PackageVersionMetadata> result = await Metadata(handler);

        Assert.Equal(["2.0.0", "1.0.0"], result.Items.Select(static m => m.Version));
        Assert.Equal(
            PackageVulnerabilitySeverity.High,
            Assert.Single(At(result, "2.0.0").Vulnerabilities).Severity);
    }

    /// <summary>
    /// nuget.org serves its registration blobs gzipped whatever the client asked for — measured: the
    /// response carries <c>Content-Encoding: gzip</c> even against an empty <c>Accept-Encoding</c>.
    /// A default <see cref="HttpClient"/> decompresses nothing, so without this the first byte is
    /// <c>0x1F</c> and a perfectly valid document fails to parse.
    /// </summary>
    [Fact]
    public async Task Metadata_DecompressesAResponseTheClientNeverAskedToBeCompressed()
    {
        var handler = new StubHandler
        {
            ["https://example.test/index.json"] = ServiceIndex,
        };

        handler.Gzip("https://example.test/registration/sample/index.json", Registration);

        FeedResult<PackageVersionMetadata> result = await Metadata(handler);

        Assert.False(result.HasErrors);
        Assert.Equal(4, result.Items.Length);
    }

    [Fact]
    public async Task Metadata_OfAPackageTheFeedDoesNotHave_IsEmptyRatherThanAFailure()
    {
        var handler = new StubHandler { ["https://example.test/index.json"] = ServiceIndex };

        FeedResult<PackageVersionMetadata> result = await Metadata(handler);

        Assert.False(result.HasErrors);
        Assert.Empty(result.Items);
    }

    /// <summary>
    /// The near miss, and the reason the resource is named with its version. A feed advertising only
    /// the unversioned <c>RegistrationsBaseUrl</c> answers documents with no deprecation and no
    /// vulnerability fields at all — measured on nuget.org, where the same package shows 32
    /// deprecated versions under 3.6.0 and none under the base resource. Reading that would report a
    /// vulnerable package as clean, so it is refused instead.
    /// </summary>
    [Fact]
    public async Task Metadata_WhenTheFeedOffersNoSemVer2Registration_RefusesRatherThanReassures()
    {
        var handler = new StubHandler
        {
            ["https://example.test/index.json"] = """
                {
                  "resources": [
                    { "@id": "https://example.test/registration/", "@type": "RegistrationsBaseUrl" }
                  ]
                }
                """,
        };

        FeedResult<PackageVersionMetadata> result = await Metadata(handler);

        Assert.True(result.HasErrors);
        Assert.Equal(
            PackageDiagnosticCodes.FeedNotUnderstood,
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Metadata_OfANonsensicalPackageId_Throws()
    {
        using var client = new HttpClient(new StubHandler());
        using var feed = new NuGetHttpFeed(client, new Uri("https://example.test/index.json"));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await feed.GetMetadataAsync(" ", TestContext.Current.CancellationToken));
    }

    private static StubHandler Feed() => new()
    {
        ["https://example.test/index.json"] = ServiceIndex,
        ["https://example.test/registration/sample/index.json"] = Registration,
    };

    private static async ValueTask<FeedResult<PackageVersionMetadata>> Metadata(StubHandler handler)
    {
        using var client = new HttpClient(handler);
        using var feed = new NuGetHttpFeed(client, new Uri("https://example.test/index.json"));

        return await feed.GetMetadataAsync("Sample", TestContext.Current.CancellationToken);
    }

    private static PackageVersionMetadata At(FeedResult<PackageVersionMetadata> result, string version) =>
        At(result.Items, version);

    private static PackageVersionMetadata At(
        ImmutableArray<PackageVersionMetadata> all, string version) =>
        Assert.Single(all.Where(m => m.Version == version));

    private static async ValueTask<FeedResult<FoundPackage>> Search(
        StubHandler handler, PackageSearchRequest request)
    {
        using var client = new HttpClient(handler);
        using var feed = new NuGetHttpFeed(client);

        return await feed.SearchAsync(request, TestContext.Current.CancellationToken);
    }

    private static async ValueTask<FeedResult<string>> Versions(StubHandler handler, string packageId)
    {
        using var client = new HttpClient(handler);
        using var feed = new NuGetHttpFeed(client);

        return await feed.GetVersionsAsync(packageId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers exactly the URLs it was given, and 404s everything else — which is what a real feed
    /// does for a package it does not have.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _compressed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Exception? Throw { get; init; }

        public string this[string url] { set => _responses[url] = value; }

        public int CountOf(string url) => _counts.GetValueOrDefault(url);

        /// <summary>Answers a URL with gzipped bytes and the header that says so.</summary>
        /// <remarks>
        /// What nuget.org's registration blobs do, whatever the request asked for.
        /// </remarks>
        public void Gzip(string url, string body)
        {
            using var buffer = new MemoryStream();

            using (var compressor = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);

                compressor.Write(bytes, 0, bytes.Length);
            }

            _compressed[url] = buffer.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Throw is not null)
            {
                throw Throw;
            }

            // AbsoluteUri rather than ToString, which unescapes %20 back to a space and would make
            // an assertion about escaping pass whether or not anything was escaped.
            string url = request.RequestUri!.AbsoluteUri;

            lock (_counts)
            {
                _counts[url] = _counts.GetValueOrDefault(url) + 1;
            }

            if (_compressed.TryGetValue(url, out byte[]? gzipped))
            {
                var content = new ByteArrayContent(gzipped);

                content.Headers.ContentEncoding.Add("gzip");

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(_responses.TryGetValue(url, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
