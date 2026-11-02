using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class ClientParsingTests
{
    [Fact]
    public void Prowlarr_ParsesReleases_AndSkipsNonTorrent()
    {
        const string json = """
            [
              { "guid": "g1", "title": "Dune.Part.Two.2024.MULTi.1080p", "size": 8589934592, "seeders": 120, "leechers": 4,
                "infoHash": "C12FE1C06BBA254A9DC9F519B335AA7C1367A88A", "downloadUrl": "http://prowlarr:9696/7/download?link=x",
                "publishDate": "2024-03-01T10:00:00Z", "protocol": "torrent" },
              { "guid": "g2", "title": "Sans hash", "size": "1000", "seeders": 3, "leechers": 1,
                "magnetUrl": "magnet:?xt=urn:btih:YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK", "protocol": "torrent" },
              { "guid": "g3", "title": "Usenet", "size": 1, "protocol": "usenet", "downloadUrl": "http://x" },
              { "guid": "g4", "title": "Aucun lien", "size": 1, "protocol": "torrent" }
            ]
            """;
        using var doc = JsonDocument.Parse(json);
        var results = ProwlarrClient.ParseSearchResults(doc.RootElement);

        Assert.Equal(2, results.Count);
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", results[0].InfoHash);
        Assert.Equal(8589934592, results[0].SizeBytes);
        Assert.Equal(120, results[0].Seeders);
        Assert.Equal(4, results[0].Leechers);
        Assert.NotNull(results[0].PublishDate);
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", results[1].InfoHash);
        Assert.Equal(1000, results[1].SizeBytes);
    }

    [Fact]
    public async Task Prowlarr_Search_SendsIndexerCategoriesAndApiKey()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("[]"));
        var client = new ProwlarrClient(http, new FakeConfig());

        await client.SearchAsync(new ProwlarrQuery("{ImdbId:tt1}", "movie", [2000, 2040]), CancellationToken.None);

        var (request, _) = Assert.Single(http.Calls);
        var url = request.RequestUri!.AbsoluteUri;
        Assert.StartsWith("http://prowlarr:9696/api/v1/search?", url, StringComparison.Ordinal);
        Assert.Contains("indexerIds=7", url, StringComparison.Ordinal);
        Assert.Contains("categories=2000&categories=2040", url, StringComparison.Ordinal);
        Assert.Contains("type=movie", url, StringComparison.Ordinal);
        Assert.Contains("query=%7BImdbId%3Att1%7D", url, StringComparison.Ordinal);
        Assert.Equal("prowlarr-key", request.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Prowlarr_Download_FollowsRedirectToMagnet()
    {
        var http = new FakeHttp((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/7/download")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("http://prowlarr:9696/other");
                return r;
            }

            var m = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            m.Headers.TryAddWithoutValidation("Location", "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
            return m;
        });
        var client = new ProwlarrClient(http, new FakeConfig());

        var source = await client.DownloadAsync("http://prowlarr:9696/7/download?link=x", CancellationToken.None);

        Assert.Null(source.File);
        Assert.StartsWith("magnet:", source.Magnet, StringComparison.Ordinal);
    }

    [Fact]
    public void Torbox_ParsesCheckCached_ObjectAndListFormats()
    {
        using var obj = JsonDocument.Parse("""
            { "success": true, "data": {
                "c12fe1c06bba254a9dc9f519b335aa7c1367a88a": { "name": "x", "size": 1, "hash": "c12fe1c06bba254a9dc9f519b335aa7c1367a88a" },
                "1111111111111111111111111111111111111111": null } }
            """);
        Assert.Equal(["c12fe1c06bba254a9dc9f519b335aa7c1367a88a"], TorboxClient.ParseCachedHashes(obj.RootElement).ToArray());

        using var list = JsonDocument.Parse("""{ "success": true, "data": [ { "hash": "C12FE1C06BBA254A9DC9F519B335AA7C1367A88A" } ] }""");
        Assert.Equal(["c12fe1c06bba254a9dc9f519b335aa7c1367a88a"], TorboxClient.ParseCachedHashes(list.RootElement).ToArray());

        using var empty = JsonDocument.Parse("""{ "success": true, "data": {} }""");
        Assert.Empty(TorboxClient.ParseCachedHashes(empty.RootElement));
    }

    [Fact]
    public void Torbox_ParsesCreateAndMyList()
    {
        using var create = JsonDocument.Parse("""{ "success": true, "data": { "torrent_id": 4242, "hash": "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", "auth_id": "x" } }""");
        var created = TorboxClient.ParseCreateResult(create.RootElement);
        Assert.Equal(4242, created.TorrentId);
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", created.Hash);

        using var mylist = JsonDocument.Parse("""
            { "success": true, "data": [
              { "id": 4242, "hash": "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", "name": "x", "progress": 0.5, "download_state": "downloading", "download_finished": false },
              { "id": 4243, "hash": "1111111111111111111111111111111111111111", "progress": 1, "download_state": "cached", "download_finished": true } ] }
            """);
        var items = TorboxClient.ParseMyList(mylist.RootElement);
        Assert.Equal(2, items.Count);
        Assert.Equal(0.5, items[0].Progress);
        Assert.False(items[0].DownloadFinished);
        Assert.True(items[1].DownloadFinished);
    }

    [Fact]
    public async Task Torbox_CreateTorrent_SendsMultipartWithBearer()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("""{ "success": true, "data": { "torrent_id": 1 } }"""));
        var client = new TorboxClient(http, new FakeConfig());

        await client.CreateTorrentAsync(new TorrentSource(null, "magnet:?xt=urn:btih:abc"), "Film", CancellationToken.None);

        var (request, body) = Assert.Single(http.Calls);
        Assert.Equal("https://api.torbox.app/v1/api/torrents/createtorrent", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("torbox-key", request.Headers.Authorization.Parameter);
        Assert.Contains("name=magnet", body, StringComparison.Ordinal);
        Assert.Contains("magnet:?xt=urn:btih:abc", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Torbox_ErrorResponse_ThrowsWithDetail()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("""{ "success": false, "error": "ACTIVE_LIMIT", "detail": "Limite de téléchargements actifs atteinte." }""", HttpStatusCode.Forbidden));
        var client = new TorboxClient(http, new FakeConfig());

        var ex = await Assert.ThrowsAsync<TorboxException>(() => client.GetMyListAsync(CancellationToken.None));
        Assert.Contains("Limite de téléchargements actifs atteinte.", ex.Message, StringComparison.Ordinal);
    }
}
