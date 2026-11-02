using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class TorrentSearchServiceTests
{
    private static IndexerProvider Indexers(FakeHttp http, FakeConfig config)
        => new(new ProwlarrClient(http, config), new TorznabClient(http, config), config);

    [Fact]
    public void Sort_PutsTorboxCachedFirst_ThenSeeders()
    {
        var sorted = TorrentSearchService.Sort(
        [
            new TorrentResult { Title = "a", Seeders = 500 },
            new TorrentResult { Title = "b", Seeders = 2, IsCachedOnTorbox = true },
            new TorrentResult { Title = "c", Seeders = 50 },
            new TorrentResult { Title = "d", Seeders = 10, IsCachedOnTorbox = true },
        ]);

        Assert.Equal(["d", "b", "a", "c"], sorted.Select(r => r.Title).ToArray());
    }

    [Fact]
    public void BuildQueries_Movie_IdThenTitle()
    {
        var q = ProwlarrClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", Year = 2021, ProviderIds = new() { ["imdb"] = "tt1160419", ["Tmdb"] = "438631" } },
            new PluginConfiguration { MovieCategories = "2000, 2040" });

        Assert.Equal(2, q.Count);
        Assert.Equal(("{ImdbId:tt1160419}", "movie"), (q[0].Query, q[0].Type));
        Assert.Equal(("Dune 2021", "search"), (q[1].Query, q[1].Type));
        Assert.Equal([2000, 2040], q[0].Categories);
    }

    [Fact]
    public void BuildQueries_SeriesEpisodeAndSeason()
    {
        var cfg = new PluginConfiguration();
        var ep = ProwlarrClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Series, Name = "The Bear", Season = 2, Episode = 3, ProviderIds = new() { ["Tvdb"] = "403245" } }, cfg);
        Assert.Equal("{TvdbId:403245}{Season:2}{Episode:3}", ep[0].Query);
        Assert.Equal("tvsearch", ep[0].Type);
        Assert.Equal("The Bear S02E03", ep[1].Query);

        var season = ProwlarrClient.BuildQueries(
            new TorrentSearchRequest { Kind = MediaKind.Series, Name = "The Bear", Season = 1 }, cfg);
        Assert.Equal("The Bear S01", Assert.Single(season).Query);
    }

    [Fact]
    public async Task Search_ComputesMissingHashes_ChecksTorbox_AndSorts()
    {
        var (torrent, _) = Bencode.MakeTorrent("Film");
        var torrentHash = TorrentHash.FromTorrentFile(torrent)!;
        var http = new FakeHttp((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v1/search", StringComparison.Ordinal))
            {
                return FakeHttp.Json("""
                    [ { "guid": "g1", "title": "Populaire", "size": 1000, "seeders": 90, "leechers": 1, "downloadUrl": "http://prowlarr:9696/7/download?id=1", "protocol": "torrent" },
                      { "guid": "g2", "title": "En cache", "size": 1000, "seeders": 3, "leechers": 0, "infoHash": "1111111111111111111111111111111111111111", "downloadUrl": "http://prowlarr:9696/7/download?id=2", "protocol": "torrent" } ]
                    """);
            }

            if (url.Contains("/download", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(torrent) };
            }

            return FakeHttp.Json("""{ "success": true, "data": { "1111111111111111111111111111111111111111": { "hash": "1111111111111111111111111111111111111111" } } }""");
        });
        var config = new FakeConfig();
        var service = new TorrentSearchService(
            Indexers(http, config), new TorboxClient(http, config), new FakeLibrary(), new SearchResultCache(), config, NullLogger<TorrentSearchService>.Instance);

        var response = await service.SearchAsync(
            new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Film", ProviderIds = new() { ["Imdb"] = "tt1" } }, CancellationToken.None);

        Assert.Equal(["En cache", "Populaire"], response.Results.Select(r => r.Title).ToArray());
        Assert.True(response.Results[0].IsCachedOnTorbox);
        Assert.True(response.Results[0].WouldAutoApprove);
        Assert.Equal(torrentHash, response.Results[1].InfoHash);
        Assert.All(response.Results, r => Assert.False(string.IsNullOrEmpty(r.Id)));

        // Le .torrent n'est téléchargé que pour le résultat sans hash.
        Assert.Single(http.Calls, c => c.Request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal));
        var check = Assert.Single(http.Calls, c => c.Request.RequestUri!.AbsolutePath.EndsWith("/checkcached", StringComparison.Ordinal));
        Assert.Contains(torrentHash, check.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_MovieAlreadyInLibrary_DoesNotQueryProwlarr()
    {
        var http = new FakeHttp((_, _) => throw new InvalidOperationException("ne doit pas être appelé"));
        var config = new FakeConfig();
        var service = new TorrentSearchService(
            Indexers(http, config), new TorboxClient(http, config), new FakeLibrary { MovieInLibrary = true }, new SearchResultCache(), config, NullLogger<TorrentSearchService>.Instance);

        var response = await service.SearchAsync(new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Film" }, CancellationToken.None);

        Assert.True(response.AlreadyInLibrary);
        Assert.Empty(response.Results);
        Assert.Empty(http.Calls);
    }
}
