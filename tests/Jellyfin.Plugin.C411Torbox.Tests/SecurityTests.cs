using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.C411Torbox.Api;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

/// <summary>Horloge réglable à la main.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Correctifs de l'audit de sécurité : secrets non exposés, cache borné, limite de recherches.</summary>
public class SecurityTests
{
    [Fact]
    public void MediaRequest_Json_NeverContainsIndexerLinks()
    {
        var request = new MediaRequest
        {
            ProwlarrGuid = "https://tracker.example/download?passkey=secret1",
            MagnetUrl = "magnet:?xt=urn:btih:x&tr=https://tracker.example/secret2/announce",
            DownloadUrl = "http://prowlarr:9696/7/download?apikey=secret3",
        };

        var json = JsonSerializer.Serialize(request, C411TorboxControllerBase.JsonOptions);

        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://prowlarr:9696/other", true)]
    [InlineData("https://tracker.example/file.torrent", false)]
    [InlineData("https://prowlarr:9696/other", false)]
    [InlineData("http://prowlarr:9697/other", false)]
    public async Task Prowlarr_Download_SendsApiKeyOnlyToSameOrigin(string redirect, bool keySent)
    {
        var (torrent, _) = Bencode.MakeTorrent("film");
        var http = new FakeHttp((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/7/download")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri(redirect);
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(torrent) };
        });
        var client = new ProwlarrClient(http, new FakeConfig());

        await client.DownloadAsync("http://prowlarr:9696/7/download?link=x", CancellationToken.None);

        Assert.Equal(2, http.Calls.Count);
        Assert.True(http.Calls[0].Request.Headers.Contains("X-Api-Key"));
        Assert.Equal(keySent, http.Calls[1].Request.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public void OpaqueCache_EvictsOldest_BeyondMaxEntries()
    {
        var time = new ManualTime();
        var cache = new OpaqueCache<string>(time, TimeSpan.FromMinutes(30), maxEntries: 3);
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            ids.Add(cache.Add("v" + i));
            time.Now += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(3, cache.Count);
        Assert.Null(cache.Get(ids[0]));
        Assert.Null(cache.Get(ids[1]));
        Assert.Equal("v4", cache.Get(ids[4]));
    }

    [Fact]
    public void SearchResultCache_BoundsTorrentFileMemory()
    {
        var time = new ManualTime();
        var cache = new SearchResultCache(time);
        var file = new byte[64 * 1024 * 1024];
        var ids = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            ids.Add(cache.Add(new CachedResult(new TorrentResult(), new TorrentSearchRequest(), new TorrentSource(file, null))));
            time.Now += TimeSpan.FromSeconds(1);
        }

        // 256 Mo maximum : seuls les 4 derniers .torrent de 64 Mo restent.
        Assert.Equal(4, cache.Count);
        Assert.Null(cache.Get(ids[1]));
        Assert.NotNull(cache.Get(ids[5]));
    }

    [Fact]
    public void OpaqueCache_ExpiredEntries_AreGone()
    {
        var time = new ManualTime();
        var cache = new MediaResultCache(time);
        var id = cache.Add(new MediaSearchResult { Name = "Dune" });

        Assert.Equal("Dune", cache.Get(id)!.Name);
        time.Now += TimeSpan.FromHours(3);
        Assert.Null(cache.Get(id));
        Assert.Null(cache.Get("inconnu"));
    }

    [Fact]
    public void TorrentSearchRequest_From_TakesOnlySeasonAndEpisodeFromBrowser()
    {
        var media = new MediaSearchResult
        {
            Kind = MediaKind.Series,
            Name = "The Bear",
            Year = 2022,
            ProviderIds = new() { ["Tmdb"] = "136315" },
        };

        var request = TorrentSearchRequest.From(media, 2, null);

        Assert.Equal(MediaKind.Series, request.Kind);
        Assert.Equal("The Bear", request.Name);
        Assert.Equal("136315", request.ProviderIds["tmdb"]);
        Assert.Equal(RequestScope.Season, request.Scope);

        // La recherche peut modifier sa requête (Validate) : la fiche en cache reste intacte.
        request.ProviderIds["Tmdb"] = "1";
        Assert.Equal("136315", media.ProviderIds["Tmdb"]);
    }

    [Fact]
    public void RateLimiter_AllowsTenPerMinute_PerUser()
    {
        var time = new ManualTime();
        var limiter = new SearchRateLimiter(time);
        var alice = Guid.NewGuid();
        for (int i = 0; i < SearchRateLimiter.MaxPerWindow; i++)
        {
            Assert.True(limiter.TryAcquire(alice));
        }

        Assert.False(limiter.TryAcquire(alice));
        Assert.True(limiter.TryAcquire(Guid.NewGuid()));

        time.Now += SearchRateLimiter.Window;
        Assert.True(limiter.TryAcquire(alice));
    }
}
