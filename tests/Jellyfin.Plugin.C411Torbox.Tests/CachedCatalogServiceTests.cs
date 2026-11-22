using System.Net;
using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

/// <summary>Fiches renvoyées par requête (« tmdb:438631 », « The Bear »…) ; les requêtes reçues sont gardées.</summary>
internal sealed class FakeMediaSearch : IMediaSearch
{
    public Dictionary<string, MediaSearchResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Query, MediaKind Kind, int? Year)> Queries { get; } = new();

    public Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, MediaKind kind, int? year, CancellationToken ct)
    {
        lock (Queries)
        {
            Queries.Add((query, kind, year));
        }

        IReadOnlyList<MediaSearchResult> found = Results.TryGetValue(year is null ? query : $"{query}|{year}", out var m) ? [m] : [];
        return Task.FromResult(found);
    }
}

public class CachedCatalogServiceTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222";
    private const string Hash3 = "3333333333333333333333333333333333333333";
    private const string Hash4 = "4444444444444444444444444444444444444444";

    private static string Item(string title, string? hash, string date, string? tmdb = null)
        => $"""
            <item>
              <title>{title}</title>
              <guid>https://c411.org/torrents/{title}</guid>
              <link>https://c411.org/api?t=get&amp;id=1</link>
              <size>4000000000</size>
              <pubDate>{date}</pubDate>
              <torznab:attr name="seeders" value="10" />
              {(hash is null ? string.Empty : $"<torznab:attr name=\"infohash\" value=\"{hash}\" />")}
              {(tmdb is null ? string.Empty : $"<torznab:attr name=\"tmdbid\" value=\"{tmdb}\" />")}
            </item>
            """;

    private static string Rss(params string[] items)
        => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>{string.Concat(items)}</channel></rss>
            """;

    private static FakeHttp Http(string rss, params string[] cachedHashes)
        => new((req, _) =>
        {
            var url = req.RequestUri!;
            if (url.AbsolutePath.EndsWith("/checkcached", StringComparison.Ordinal))
            {
                var data = string.Join(",", cachedHashes.Select(h => $"\"{h}\": {{ \"hash\": \"{h}\" }}"));
                return FakeHttp.Json($$"""{ "success": true, "data": { {{data}} } }""");
            }

            // Deuxième page vide.
            return url.Query.Contains("offset=", StringComparison.Ordinal) ? FakeHttp.Json(Rss()) : FakeHttp.Json(rss);
        });

    private static (CachedCatalogService Service, ManualTime Time) Create(FakeHttp http, FakeMediaSearch media)
    {
        var config = new FakeConfig();
        config.Current.IndexerMode = IndexerMode.Torznab;
        var time = new ManualTime { Now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero) };
        var indexers = new IndexerProvider(new ProwlarrClient(http, config), new TorznabClient(http, config), config);
        var service = new CachedCatalogService(indexers, new TorboxClient(http, config), media, config, NullLogger<CachedCatalogService>.Instance, time);
        return (service, time);
    }

    private static MediaSearchResult Media(string name, int year, string tmdb, MediaKind kind = MediaKind.Movie, bool inLibrary = false)
        => new() { Id = "id-" + tmdb, Kind = kind, Name = name, Year = year, ProviderIds = new() { ["Tmdb"] = tmdb }, InLibrary = inLibrary };

    [Fact]
    public async Task Movies_KeepsOnlyCachedReleases_GroupedByMedia()
    {
        var rss = Rss(
            Item("Dune.Part.Two.2024.MULTi.1080p.WEB.x264-GRP", Hash1, "Sun, 22 Nov 2026 10:00:00 +0000", tmdb: "693134"),
            Item("Dune.Deuxieme.Partie.2024.VFF.2160p.BluRay", Hash2, "Sun, 22 Nov 2026 09:00:00 +0000"),
            Item("Wicked.2024.MULTi.1080p", Hash3, "Sun, 22 Nov 2026 11:00:00 +0000"),
            Item("Gladiator.II.2024.MULTi.1080p", Hash4, "Sun, 22 Nov 2026 08:00:00 +0000"),
            Item("Sans.Hash.2024.1080p", null, "Sun, 22 Nov 2026 07:00:00 +0000"));
        var http = Http(rss, Hash1, Hash2, Hash3);
        var media = new FakeMediaSearch();
        var dune = Media("Dune : Deuxième partie", 2024, "693134");
        media.Results["tmdb:693134"] = dune;
        media.Results["Dune Deuxieme Partie|2024"] = dune;
        var (service, _) = Create(http, media);

        var catalog = await service.GetAsync(MediaKind.Movie, refresh: false, CancellationToken.None);

        // Gladiator n'est pas en cache ; « Wicked » n'a pas de fiche : il reste, avec le titre lu dans le torrent.
        Assert.Equal(5, catalog.Scanned);
        Assert.Equal(["Wicked", "Dune : Deuxième partie"], catalog.Items.Select(i => i.Name).ToArray());
        var wicked = catalog.Items[0];
        Assert.Null(wicked.Media);
        Assert.Equal(2024, wicked.Year);

        // Les deux torrents de Dune (avec et sans identifiant) mènent à la même fiche.
        var item = catalog.Items[1];
        Assert.Same(dune, item.Media);
        Assert.Equal(2, item.ReleaseCount);
        Assert.Equal("Dune.Part.Two.2024.MULTi.1080p.WEB.x264-GRP", item.Releases[0].Title);
        Assert.Contains("sans infohash", catalog.Warning, StringComparison.Ordinal);

        // Dernières sorties : recherche sans texte dans les catégories films.
        var latest = http.Calls.First(c => c.Request.RequestUri!.Host == "c411.org").Request.RequestUri!.Query;
        Assert.Contains("t=search", latest, StringComparison.Ordinal);
        Assert.Contains("cat=2000%2C2030%2C2060%2C2070", latest, StringComparison.Ordinal);
        Assert.DoesNotContain("q=", latest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Series_ListsSeasons_AndRetriesTitleWithoutYear()
    {
        var rss = Rss(
            Item("The.Bear.2022.S03E02.MULTi.1080p.WEB", Hash1, "Sun, 22 Nov 2026 10:00:00 +0000"),
            Item("The.Bear.S01.COMPLETE.FRENCH.720p", Hash2, "Sun, 22 Nov 2026 09:00:00 +0000"));
        var http = Http(rss, Hash1, Hash2);
        var media = new FakeMediaSearch();
        var bear = Media("The Bear", 2022, "136315", MediaKind.Series);
        media.Results["The Bear"] = bear;
        var (service, _) = Create(http, media);

        var catalog = await service.GetAsync(MediaKind.Series, refresh: false, CancellationToken.None);

        var item = Assert.Single(catalog.Items);
        Assert.Same(bear, item.Media);
        Assert.Equal([1, 3], item.Seasons);
        Assert.Equal((3, 2), (item.Releases[0].Season!.Value, item.Releases[0].Episode!.Value));
        Assert.Contains(media.Queries, q => q is { Query: "The Bear", Year: 2022 });
        Assert.Contains(media.Queries, q => q is { Query: "The Bear", Year: null });
    }

    [Fact]
    public async Task Catalog_IsCached_AndRefreshIsThrottled()
    {
        var http = Http(Rss(Item("Wicked.2024.MULTi.1080p", Hash1, "Sun, 22 Nov 2026 11:00:00 +0000")), Hash1);
        var (service, time) = Create(http, new FakeMediaSearch());

        var first = await service.GetAsync(MediaKind.Movie, false, CancellationToken.None);
        var calls = http.Calls.Count;

        Assert.Same(first, await service.GetAsync(MediaKind.Movie, false, CancellationToken.None));
        Assert.Same(first, await service.GetAsync(MediaKind.Movie, true, CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);

        time.Now += CachedCatalogService.MinRefreshInterval;
        Assert.NotSame(first, await service.GetAsync(MediaKind.Movie, true, CancellationToken.None));

        var second = http.Calls.Count;
        time.Now += CachedCatalogService.Lifetime;
        await service.GetAsync(MediaKind.Movie, false, CancellationToken.None);
        Assert.True(http.Calls.Count > second);
    }

    [Fact]
    public async Task Books_AreRejected_AndTorboxKeyIsRequired()
    {
        var http = new FakeHttp((_, _) => throw new InvalidOperationException("ne doit pas être appelé"));
        var (service, _) = Create(http, new FakeMediaSearch());
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(MediaKind.Book, false, CancellationToken.None));

        var config = new FakeConfig();
        config.Current.TorboxApiKey = string.Empty;
        var indexers = new IndexerProvider(new ProwlarrClient(http, config), new TorznabClient(http, config), config);
        var noKey = new CachedCatalogService(indexers, new TorboxClient(http, config), new FakeMediaSearch(), config, NullLogger<CachedCatalogService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noKey.GetAsync(MediaKind.Movie, false, CancellationToken.None));
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task Prowlarr_LatestUsesEmptyQueryWithOffset()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("[]"));
        var config = new FakeConfig();
        await new ProwlarrClient(http, config).LatestAsync(MediaKind.Series, 100, 100, CancellationToken.None);

        var query = Assert.Single(http.Calls).Request.RequestUri!.Query;
        Assert.Contains("limit=100&offset=100&query=&type=search", query, StringComparison.Ordinal);
        Assert.Contains("categories=5000", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Torznab_LatestQuery()
    {
        var cfg = new PluginConfiguration { TvCategories = "5000, 5070" };
        Assert.Equal("t=search&limit=100&cat=5000,5070", TorznabClient.BuildLatestQuery(MediaKind.Series, 0, 100, cfg).Describe());
        Assert.Equal("t=search&limit=100&offset=100&cat=5000,5070", TorznabClient.BuildLatestQuery(MediaKind.Series, 100, 100, cfg).Describe());
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.MULTi.1080p.WEB.x264-GRP", "Dune Part Two", 2024, null, null)]
    [InlineData("1917.2019.MULTi.2160p.UHD.BluRay", "1917", 2019, null, null)]
    [InlineData("The.Bear.S02E03.MULTi.1080p.WEB", "The Bear", null, 2, 3)]
    [InlineData("The Bear (2022) S01 COMPLETE FRENCH 720p", "The Bear", 2022, 1, null)]
    [InlineData("Les.Simpson.Saison.35.FRENCH.720p", "Les Simpson", null, 35, null)]
    [InlineData("Astérix.Le.Domaine.Des.Dieux.FRENCH.1080p", "Astérix Le Domaine Des Dieux", null, null, null)]
    public void ReleaseTitle_Parse(string release, string name, int? year, int? season, int? episode)
    {
        var info = ReleaseTitle.Parse(release);
        Assert.Equal((name, year, season, episode), (info.Name, info.Year, info.Season, info.Episode));
    }

    [Theory]
    [InlineData("tt0068646", "tt0068646")]
    [InlineData("68646", "tt0068646")]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    public void NormalizeImdb(string input, string? expected)
        => Assert.Equal(expected, CachedCatalogService.NormalizeImdb(input));
}
