using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
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
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Exception? Throw { get; init; }

        public string this[string url] { set => _responses[url] = value; }

        public int CountOf(string url) => _counts.GetValueOrDefault(url);

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

            return Task.FromResult(_responses.TryGetValue(url, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
