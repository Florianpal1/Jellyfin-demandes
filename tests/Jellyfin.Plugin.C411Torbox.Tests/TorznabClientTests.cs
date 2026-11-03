using System.Net;
using System.Xml.Linq;
using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class TorznabClientTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Dune.2021.MULTi.1080p.BluRay</title>
              <guid>https://c411.org/torrents/123</guid>
              <link>https://c411.org/api?t=get&amp;id=123&amp;apikey=secret</link>
              <size>8589934592</size>
              <pubDate>Mon, 02 Nov 2026 10:00:00 +0000</pubDate>
              <enclosure url="https://c411.org/api?t=get&amp;id=123&amp;apikey=secret" length="8589934592" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="120" />
              <torznab:attr name="peers" value="130" />
              <torznab:attr name="infohash" value="C12FE1C06BBA254A9DC9F519B335AA7C1367A88A" />
            </item>
            <item>
              <title>Dune.2021.720p</title>
              <guid>https://c411.org/torrents/124</guid>
              <link>https://c411.org/api?t=get&amp;id=124</link>
              <torznab:attr name="size" value="2000" />
              <torznab:attr name="seeders" value="3" />
              <torznab:attr name="leechers" value="7" />
            </item>
          </channel>
        </rss>
        """;

    private static FakeConfig TorznabConfig()
    {
        var config = new FakeConfig();
        config.Current.IndexerMode = IndexerMode.Torznab;
        return config;
    }

    [Fact]
    public void ParseResults_ReadsSizeSeedersLeechersAndHash()
    {
        var results = TorznabClient.ParseResults(XDocument.Parse(Rss));

        Assert.Equal(2, results.Count);
        var first = results[0];
        Assert.Equal("Dune.2021.MULTi.1080p.BluRay", first.Title);
        Assert.Equal(8589934592, first.SizeBytes);
        Assert.Equal(120, first.Seeders);
        Assert.Equal(10, first.Leechers); // peers − seeders
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", first.InfoHash);
        Assert.Equal("https://c411.org/api?t=get&id=123&apikey=secret", first.DownloadUrl);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), first.PublishDate);

        var second = results[1];
        Assert.Equal(2000, second.SizeBytes);
        Assert.Equal(7, second.Leechers);
        Assert.Null(second.InfoHash);
    }

    [Theory]
    [InlineData("https://c411.org", "https://c411.org/api")]
    [InlineData("https://c411.org/", "https://c411.org/api")]
    [InlineData("https://c411.org/api", "https://c411.org/api")]
    [InlineData("https://c411.org/api/torznab/", "https://c411.org/api/torznab")]
    public void BuildEndpoint_AddsApiPathWhenMissing(string configured, string expected)
        => Assert.Equal(expected, TorznabClient.BuildEndpoint(new Uri(configured)));

    [Fact]
    public void BuildQueries_Movie_TmdbThenImdbThenTitle()
    {
        var q = TorznabClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", Year = 2021, ProviderIds = new() { ["Tmdb"] = "438631", ["Imdb"] = "tt1160419" } },
            new PluginConfiguration { MovieCategories = "2000,2030" });

        Assert.Equal(3, q.Count);
        Assert.Equal("t=movie&limit=100&cat=2000,2030&tmdbid=438631", q[0].Describe());
        Assert.Equal("t=movie&limit=100&cat=2000,2030&imdbid=1160419", q[1].Describe());
        Assert.Equal("t=search&limit=100&cat=2000,2030&q=Dune 2021", q[2].Describe());
    }

    [Fact]
    public void BuildQueries_Series_SeasonAndEpisode()
    {
        var q = TorznabClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Series, Name = "The Bear", Season = 2, Episode = 3, ProviderIds = new() { ["Tmdb"] = "136315" } },
            new PluginConfiguration { TvCategories = "5000" });

        Assert.Equal("t=tvsearch&limit=100&cat=5000&tmdbid=136315&season=2&ep=3", q[0].Describe());
        Assert.Equal("t=search&limit=100&cat=5000&q=The Bear S02E03", q[1].Describe());
    }

    [Fact]
    public void BuildQueries_Book_TitleOnlyInBookCategories()
    {
        var q = TorznabClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Book, Name = "Astérix le Gaulois", Year = 1961, ProviderIds = new() { ["GoogleBooks"] = "gb1" } },
            new PluginConfiguration { BookCategories = "7000,7030" });

        Assert.Equal("t=search&limit=100&cat=7000,7030&q=Astérix le Gaulois", Assert.Single(q).Describe());
    }

    [Fact]
    public async Task Search_SendsApiKey_AndFallsBackToTitleWhenIdSearchIsEmpty()
    {
        var http = new FakeHttp((req, _) => req.RequestUri!.Query.Contains("tmdbid", StringComparison.Ordinal)
            ? FakeHttp.Json("""<rss><channel></channel></rss>""")
            : FakeHttp.Json(Rss));
        var client = new TorznabClient(http, TorznabConfig());

        var results = await client.SearchAsync(
            new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", Year = 2021, ProviderIds = new() { ["Tmdb"] = "438631" } },
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, http.Calls.Count);
        var first = http.Calls[0].Request.RequestUri!;
        Assert.Equal("https://c411.org/api", first.GetLeftPart(UriPartial.Path));
        Assert.Contains("apikey=c411-key", first.Query, StringComparison.Ordinal);
        Assert.Contains("q=Dune%202021", http.Calls[1].Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidApiKey_IsExplicit()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("""<?xml version="1.0"?><error code="100" description="Invalid API Key" />""", HttpStatusCode.Unauthorized));
        var client = new TorznabClient(http, TorznabConfig());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.TestAsync(CancellationToken.None));

        Assert.Equal("C411 a refusé la clé API (Invalid API Key).", ex.Message);
    }

    [Fact]
    public async Task HtmlInsteadOfXml_ExplainsWrongUrl()
    {
        var http = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Accueil") });
        var client = new TorznabClient(http, TorznabConfig());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.TestAsync(CancellationToken.None));

        Assert.Contains("réponse illisible", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchService_UsesTorznab_WhenConfigured()
    {
        var http = new FakeHttp((req, _) => req.RequestUri!.Host == "c411.org"
            ? FakeHttp.Json(Rss)
            : FakeHttp.Json("""{ "success": true, "data": {} }"""));
        var config = TorznabConfig();
        var indexers = new IndexerProvider(new ProwlarrClient(http, config), new TorznabClient(http, config), config);
        var service = new TorrentSearchService(indexers, new TorboxClient(http, config), new FakeLibrary(), new SearchResultCache(), config, NullLogger<TorrentSearchService>.Instance);

        var response = await service.SearchAsync(
            new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", ProviderIds = new() { ["Tmdb"] = "438631" } }, CancellationToken.None);

        Assert.Equal(["Dune.2021.MULTi.1080p.BluRay", "Dune.2021.720p"], response.Results.Select(r => r.Title).ToArray());
        Assert.DoesNotContain(http.Calls, c => c.Request.RequestUri!.Host == "prowlarr");
    }
}
