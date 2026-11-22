using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Page « En cache » : les dernières sorties C411 (films ou séries) déjà en cache sur Torbox, regroupées par média.
/// Torbox ne sait pas lister son cache : on lui soumet les infohash des dernières sorties. Le résultat est gardé
/// en mémoire (commun à tous les utilisateurs) pour ménager le quota de la clé C411.
/// </summary>
public class CachedCatalogService
{
    /// <summary>Dernières sorties examinées : <see cref="Pages"/> pages de <see cref="PageSize"/>.</summary>
    public const int Pages = 2;

    public const int PageSize = 100;

    /// <summary>Médias au plus (chacun demande une recherche de fiche).</summary>
    public const int MaxItems = 80;

    public const int MaxReleasesPerItem = 10;

    private const int CheckCachedBatch = 100;

    private const int MaxParallelLookups = 4;

    /// <summary>Durée de vie du catalogue en mémoire (les fiches restent valables 2 h dans <see cref="MediaResultCache"/>).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>« Actualiser » ne relance pas la recherche si le catalogue a moins de 2 minutes.</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly IndexerProvider _indexers;
    private readonly TorboxClient _torbox;
    private readonly IMediaSearch _media;
    private readonly IConfigProvider _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CachedCatalogService> _logger;
    private readonly Dictionary<MediaKind, CatalogResponse> _catalogs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CachedCatalogService(
        IndexerProvider indexers,
        TorboxClient torbox,
        IMediaSearch media,
        IConfigProvider config,
        ILogger<CachedCatalogService> logger)
        : this(indexers, torbox, media, config, logger, TimeProvider.System)
    {
    }

    public CachedCatalogService(
        IndexerProvider indexers,
        TorboxClient torbox,
        IMediaSearch media,
        IConfigProvider config,
        ILogger<CachedCatalogService> logger,
        TimeProvider time)
    {
        _indexers = indexers;
        _torbox = torbox;
        _media = media;
        _config = config;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Catalogue du type (films ou séries). <paramref name="refresh"/> force une nouvelle recherche,
    /// sauf si le catalogue vient d'être construit.
    /// </summary>
    public async Task<CatalogResponse> GetAsync(MediaKind kind, bool refresh, CancellationToken ct)
    {
        if (kind is not (MediaKind.Movie or MediaKind.Series))
        {
            throw new ArgumentException("Seuls les films et les séries ont une page « En cache ».");
        }

        if (string.IsNullOrWhiteSpace(_config.Current.TorboxApiKey))
        {
            throw new InvalidOperationException("La clé API Torbox n'est pas configurée.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_catalogs.TryGetValue(kind, out var current))
            {
                var age = now - current.GeneratedAt;
                if (age < (refresh ? MinRefreshInterval : Lifetime))
                {
                    return current;
                }
            }

            var built = await BuildAsync(kind, ct).ConfigureAwait(false);
            _catalogs[kind] = built;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CatalogResponse> BuildAsync(MediaKind kind, CancellationToken ct)
    {
        var warnings = new List<string>();
        var indexer = _indexers.Current;

        // 1. Dernières sorties (sans doublon).
        var latest = new List<TorrentResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int page = 0; page < Pages; page++)
        {
            var results = await indexer.LatestAsync(kind, page * PageSize, PageSize, ct).ConfigureAwait(false);
            foreach (var r in results)
            {
                if (seen.Add(r.InfoHash ?? r.Guid ?? r.Title))
                {
                    latest.Add(r);
                }
            }

            if (results.Count == 0)
            {
                break;
            }
        }

        // 2. Cache Torbox. Les torrents sans infohash ne sont pas téléchargés (chaque .torrent compte sur le quota C411).
        var withHash = latest.Where(r => r.InfoHash is not null).ToList();
        if (withHash.Count < latest.Count)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{latest.Count - withHash.Count} sortie(s) sans infohash non vérifiée(s)."));
        }

        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in withHash.Select(r => r.InfoHash!).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(CheckCachedBatch))
        {
            cached.UnionWith(await _torbox.CheckCachedAsync(batch, ct).ConfigureAwait(false));
        }

        var hits = withHash.Where(r => cached.Contains(r.InfoHash!)).ToList();

        // 3. Regroupement par média : identifiant donné par l'indexeur, sinon titre (et année pour un film) lu dans le nom.
        var groups = hits
            .Select(r => (Release: r, Info: ReleaseTitle.Parse(r.Title)))
            .GroupBy(x => GroupKey(kind, x.Release, x.Info), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Release.PublishDate ?? DateTime.MinValue).ToList())
            .OrderByDescending(g => g[0].Release.PublishDate ?? DateTime.MinValue)
            .ToList();
        if (groups.Count > MaxItems)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Seuls les {MaxItems} médias les plus récents sont affichés."));
            groups = groups.Take(MaxItems).ToList();
        }

        // 4. Fiche de chaque média (affiche, synopsis, identifiants pour la demande).
        var medias = new ConcurrentDictionary<int, MediaSearchResult>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, groups.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = ct },
            async (i, token) =>
            {
                var first = groups[i][0];
                try
                {
                    var media = await ResolveAsync(kind, first.Release, first.Info, token).ConfigureAwait(false);
                    if (media is not null)
                    {
                        medias[i] = media;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Fiche introuvable pour « {Title} »", first.Release.Title);
                }
            }).ConfigureAwait(false);

        // 5. Deux groupes peuvent mener à la même fiche (l'un avec identifiant, l'autre sans) : ils sont fusionnés.
        var items = new List<CatalogItem>();
        var byMedia = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        var releasesOf = new Dictionary<CatalogItem, List<(TorrentResult Release, ReleaseTitleInfo Info)>>();
        for (int i = 0; i < groups.Count; i++)
        {
            medias.TryGetValue(i, out var media);
            var key = media is null ? null : MediaKey(media);
            if (key is null || !byMedia.TryGetValue(key, out var item))
            {
                var info = groups[i][0].Info;
                item = new CatalogItem
                {
                    Kind = kind,
                    Media = media,
                    Name = media?.Name ?? info.Name,
                    Year = media is null ? (kind == MediaKind.Movie ? info.Year : null) : media.Year,
                };
                items.Add(item);
                releasesOf[item] = new();
                if (key is not null)
                {
                    byMedia[key] = item;
                }
            }

            releasesOf[item].AddRange(groups[i]);
        }

        foreach (var item in items)
        {
            var releases = releasesOf[item].OrderByDescending(x => x.Release.PublishDate ?? DateTime.MinValue).ToList();
            item.ReleaseCount = releases.Count;
            item.LatestPublishDate = releases[0].Release.PublishDate;
            item.Seasons = kind == MediaKind.Series
                ? releases.Select(x => x.Info.Season).OfType<int>().Distinct().Order().ToList()
                : Array.Empty<int>();
            item.Releases = releases.Take(MaxReleasesPerItem).Select(x => new CatalogRelease
            {
                Title = x.Release.Title,
                SizeBytes = x.Release.SizeBytes,
                Seeders = x.Release.Seeders,
                Season = kind == MediaKind.Series ? x.Info.Season : null,
                Episode = kind == MediaKind.Series ? x.Info.Episode : null,
                PublishDate = x.Release.PublishDate,
            }).ToList();
        }

        return new CatalogResponse
        {
            Kind = kind,
            Items = items.OrderByDescending(i => i.LatestPublishDate ?? DateTime.MinValue).ToList(),
            Scanned = latest.Count,
            GeneratedAt = _time.GetUtcNow().UtcDateTime,
            Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null,
        };
    }

    internal static string GroupKey(MediaKind kind, TorrentResult r, ReleaseTitleInfo info)
    {
        if (r.TmdbId is not null)
        {
            return "tmdb:" + r.TmdbId;
        }

        if (NormalizeImdb(r.ImdbId) is { } imdb)
        {
            return "imdb:" + imdb;
        }

        if (kind == MediaKind.Series && r.TvdbId is not null)
        {
            return "tvdb:" + r.TvdbId;
        }

        var name = info.Name.ToLowerInvariant();
        return kind == MediaKind.Movie && info.Year is not null
            ? string.Create(CultureInfo.InvariantCulture, $"name:{name}|{info.Year}")
            : "name:" + name;
    }

    /// <summary>IMDb « tt0068646 » ; les indexeurs le donnent souvent sans préfixe (« 68646 »).</summary>
    internal static string? NormalizeImdb(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var v = id.Trim().ToLowerInvariant();
        if (v.StartsWith("tt", StringComparison.Ordinal))
        {
            v = v[2..];
        }

        return v.Length > 0 && v.All(char.IsAsciiDigit) && v.TrimStart('0').Length > 0 ? "tt" + v.PadLeft(7, '0') : null;
    }

    private async Task<MediaSearchResult?> ResolveAsync(MediaKind kind, TorrentResult r, ReleaseTitleInfo info, CancellationToken ct)
    {
        var idQuery = r.TmdbId is not null ? "tmdb:" + r.TmdbId
            : NormalizeImdb(r.ImdbId) is { } imdb ? "imdb:" + imdb
            : kind == MediaKind.Series && r.TvdbId is not null ? "tvdb:" + r.TvdbId
            : null;
        if (idQuery is not null)
        {
            var byId = (await _media.SearchAsync(idQuery, kind, null, ct).ConfigureAwait(false)).FirstOrDefault(m => !m.IsFreeText);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (info.Name.Length < 2)
        {
            return null;
        }

        // L'année d'une release est parfois décalée d'un an (sortie en salle / en vidéo) : nouvel essai sans elle.
        var found = (await _media.SearchAsync(info.Name, kind, info.Year, ct).ConfigureAwait(false)).FirstOrDefault(m => !m.IsFreeText);
        if (found is null && info.Year is not null)
        {
            found = (await _media.SearchAsync(info.Name, kind, null, ct).ConfigureAwait(false)).FirstOrDefault(m => !m.IsFreeText);
        }

        return found;
    }

    private static string? MediaKey(MediaSearchResult media)
    {
        if (media.ProviderIds.TryGetValue("Tmdb", out var tmdb))
        {
            return "tmdb:" + tmdb;
        }

        if (media.ProviderIds.TryGetValue("Imdb", out var imdb))
        {
            return "imdb:" + imdb;
        }

        return media.ProviderIds.TryGetValue("Tvdb", out var tvdb) ? "tvdb:" + tvdb : null;
    }
}
