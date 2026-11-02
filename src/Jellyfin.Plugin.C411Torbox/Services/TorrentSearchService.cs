using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Recherche de torrents pour un média : Prowlarr (C411) → infohash → cache Torbox → tri.
/// Tri : en cache Torbox d'abord, puis seeders décroissants.
/// </summary>
public class TorrentSearchService
{
    private const int MaxParallelDownloads = 4;

    private readonly ProwlarrClient _prowlarr;
    private readonly TorboxClient _torbox;
    private readonly ILibraryLookup _library;
    private readonly SearchResultCache _cache;
    private readonly IConfigProvider _config;
    private readonly ILogger<TorrentSearchService> _logger;

    public TorrentSearchService(
        ProwlarrClient prowlarr,
        TorboxClient torbox,
        ILibraryLookup library,
        SearchResultCache cache,
        IConfigProvider config,
        ILogger<TorrentSearchService> logger)
    {
        _prowlarr = prowlarr;
        _torbox = torbox;
        _library = library;
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<TorrentSearchResponse> SearchAsync(TorrentSearchRequest request, CancellationToken ct)
    {
        Validate(request);
        var cfg = _config.Current;
        var response = new TorrentSearchResponse { Scope = request.Scope };
        var warnings = new List<string>();

        switch (request.Scope)
        {
            case RequestScope.Movie:
                response.AlreadyInLibrary = _library.IsMovieInLibrary(request.ProviderIds);
                break;
            case RequestScope.Episode:
                response.AlreadyInLibrary = _library.CountEpisodes(request.ProviderIds, request.Season!.Value, request.Episode) > 0;
                break;
            case RequestScope.Season:
                response.EpisodesInLibrary = _library.CountEpisodes(request.ProviderIds, request.Season!.Value, null);
                break;
        }

        if (response.AlreadyInLibrary)
        {
            return response;
        }

        var results = await QueryProwlarrAsync(request, ct).ConfigureAwait(false);
        var sources = await FillMissingHashesAsync(results, cfg.HashLookupLimit, warnings, ct).ConfigureAwait(false);

        var hashes = results.Select(r => r.InfoHash).OfType<string>().ToList();
        if (hashes.Count > 0 && !string.IsNullOrWhiteSpace(cfg.TorboxApiKey))
        {
            try
            {
                var cached = await _torbox.CheckCachedAsync(hashes, ct).ConfigureAwait(false);
                foreach (var r in results)
                {
                    r.IsCachedOnTorbox = r.InfoHash is not null && cached.Contains(r.InfoHash);
                }
            }
            catch (TorboxException ex)
            {
                _logger.LogWarning(ex, "Vérification du cache Torbox impossible");
                warnings.Add("Le cache Torbox n'a pas pu être vérifié.");
            }
        }

        foreach (var r in results)
        {
            r.WouldAutoApprove = ApprovalRules.Evaluate(request.Scope, r.SizeBytes, r.Seeders, r.IsCachedOnTorbox, cfg) != AutoApprovalReason.None;
            sources.TryGetValue(r, out var source);
            r.Id = _cache.Add(new CachedResult(r, request, source));
        }

        response.Results = Sort(results);
        response.Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        return response;
    }

    /// <summary>En cache Torbox d'abord, puis seeders décroissants (puis leechers, puis date).</summary>
    public static IReadOnlyList<TorrentResult> Sort(IEnumerable<TorrentResult> results)
        => results
            .OrderByDescending(r => r.IsCachedOnTorbox)
            .ThenByDescending(r => r.Seeders)
            .ThenByDescending(r => r.Leechers)
            .ThenByDescending(r => r.PublishDate ?? DateTime.MinValue)
            .ToList();

    /// <summary>
    /// Requêtes Prowlarr à essayer dans l'ordre : par identifiant d'abord (plus précis),
    /// puis par titre si l'indexeur ne supporte pas la recherche par identifiant.
    /// </summary>
    public static IReadOnlyList<ProwlarrQuery> BuildQueries(TorrentSearchRequest request, Configuration.PluginConfiguration cfg)
    {
        var ids = LibraryLookup.FilterIds(request.ProviderIds);
        var queries = new List<ProwlarrQuery>();
        if (request.Kind == MediaKind.Movie)
        {
            var cats = JsonElementExtensions.ParseIntList(cfg.MovieCategories).ToList();
            if (ids.TryGetValue("Imdb", out var imdb))
            {
                queries.Add(new ProwlarrQuery($"{{ImdbId:{imdb}}}", "movie", cats));
            }
            else if (ids.TryGetValue("Tmdb", out var tmdb))
            {
                queries.Add(new ProwlarrQuery($"{{TmdbId:{tmdb}}}", "movie", cats));
            }

            var text = request.Year.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{request.Name} {request.Year}")
                : request.Name;
            queries.Add(new ProwlarrQuery(text, "search", cats));
        }
        else
        {
            var cats = JsonElementExtensions.ParseIntList(cfg.TvCategories).ToList();
            var season = request.Season!.Value;
            var epToken = request.Episode.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{{Episode:{request.Episode.Value}}}")
                : string.Empty;
            var seasonToken = string.Create(CultureInfo.InvariantCulture, $"{{Season:{season}}}");
            if (ids.TryGetValue("Tvdb", out var tvdb))
            {
                queries.Add(new ProwlarrQuery($"{{TvdbId:{tvdb}}}{seasonToken}{epToken}", "tvsearch", cats));
            }
            else if (ids.TryGetValue("Imdb", out var imdb))
            {
                queries.Add(new ProwlarrQuery($"{{ImdbId:{imdb}}}{seasonToken}{epToken}", "tvsearch", cats));
            }

            var sxx = request.Episode.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{request.Episode.Value:00}")
                : string.Create(CultureInfo.InvariantCulture, $"S{season:00}");
            queries.Add(new ProwlarrQuery($"{request.Name} {sxx}", "search", cats));
        }

        return queries;
    }

    private static void Validate(TorrentSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Le titre du média est obligatoire.");
        }

        if (request.Kind == MediaKind.Series && (request.Season is null or < 0 || request.Episode is < 1))
        {
            throw new ArgumentException("Numéro de saison ou d'épisode invalide.");
        }

        if (request.Kind == MediaKind.Movie)
        {
            request.Season = null;
            request.Episode = null;
        }
    }

    private async Task<List<TorrentResult>> QueryProwlarrAsync(TorrentSearchRequest request, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var query in BuildQueries(request, _config.Current))
        {
            try
            {
                var results = await _prowlarr.SearchAsync(query, ct).ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return results.ToList();
                }
            }
            catch (HttpRequestException ex)
            {
                // Une recherche par identifiant peut être refusée si C411 ne la supporte pas : on passe à la suivante.
                _logger.LogWarning(ex, "Recherche Prowlarr échouée pour « {Query} »", query.Query);
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new HttpRequestException(lastError.Message, lastError);
        }

        return new List<TorrentResult>();
    }

    /// <summary>
    /// Calcule l'infohash manquant en téléchargeant le .torrent, pour les N meilleurs résultats seulement
    /// (chaque téléchargement peut compter sur C411). Les fichiers sont gardés pour un envoi ultérieur.
    /// </summary>
    private async Task<Dictionary<TorrentResult, TorrentSource>> FillMissingHashesAsync(
        List<TorrentResult> results,
        int limit,
        List<string> warnings,
        CancellationToken ct)
    {
        var sources = new ConcurrentDictionary<TorrentResult, TorrentSource>();
        var missing = results.Where(r => r.InfoHash is null && r.DownloadUrl is not null).ToList();
        if (missing.Count == 0)
        {
            return new Dictionary<TorrentResult, TorrentSource>();
        }

        var toFetch = missing.OrderByDescending(r => r.Seeders).Take(Math.Max(0, limit)).ToList();
        await Parallel.ForEachAsync(
            toFetch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = ct },
            async (r, token) =>
            {
                try
                {
                    var source = await _prowlarr.DownloadAsync(r.DownloadUrl!, token).ConfigureAwait(false);
                    r.InfoHash = source.File is not null ? TorrentHash.FromTorrentFile(source.File) : TorrentHash.FromMagnet(source.Magnet);
                    sources[r] = source;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Impossible de récupérer le .torrent de « {Title} »", r.Title);
                }
            }).ConfigureAwait(false);

        if (missing.Count > toFetch.Count)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Cache Torbox non vérifié pour {missing.Count - toFetch.Count} résultat(s) peu seedé(s)."));
        }

        return new Dictionary<TorrentResult, TorrentSource>(sources);
    }
}
