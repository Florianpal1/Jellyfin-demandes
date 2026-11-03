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
/// Recherche de torrents pour un média : C411 (Torznab direct ou via Prowlarr) → infohash → cache Torbox → tri.
/// Tri : en cache Torbox d'abord, puis seeders décroissants.
/// </summary>
public class TorrentSearchService
{
    private const int MaxParallelDownloads = 4;

    private readonly IndexerProvider _indexers;
    private readonly TorboxClient _torbox;
    private readonly ILibraryLookup _library;
    private readonly SearchResultCache _cache;
    private readonly IConfigProvider _config;
    private readonly ITitleAliasProvider _aliases;
    private readonly ILogger<TorrentSearchService> _logger;

    public TorrentSearchService(
        IndexerProvider indexers,
        TorboxClient torbox,
        ILibraryLookup library,
        SearchResultCache cache,
        IConfigProvider config,
        ITitleAliasProvider aliases,
        ILogger<TorrentSearchService> logger)
    {
        _indexers = indexers;
        _torbox = torbox;
        _library = library;
        _cache = cache;
        _config = config;
        _aliases = aliases;
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
            case RequestScope.Book:
                response.AlreadyInLibrary = _library.IsBookInLibrary(request.ProviderIds);
                break;
        }

        if (response.AlreadyInLibrary)
        {
            return response;
        }

        try
        {
            request.AlternateNames = await _aliases.GetAlternateNamesAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Titres alternatifs de « {Name} » indisponibles", request.Name);
        }

        // Les torrents sans rapport avec le média (identifiant, titre, année, saison ou épisode différents) sont écartés.
        var matcher = new ReleaseMatcher(request);
        var indexer = _indexers.Current;
        var results = (await indexer.SearchAsync(request, matcher.IsRelevant, ct).ConfigureAwait(false)).ToList();
        var sources = await FillMissingHashesAsync(indexer, results, cfg.HashLookupLimit, warnings, ct).ConfigureAwait(false);

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

        if (request.Kind is MediaKind.Movie or MediaKind.Book)
        {
            request.Season = null;
            request.Episode = null;
        }
    }

    /// <summary>
    /// Calcule l'infohash manquant en téléchargeant le .torrent, pour les N meilleurs résultats seulement
    /// (chaque téléchargement peut compter sur C411). Les fichiers sont gardés pour un envoi ultérieur.
    /// </summary>
    private async Task<Dictionary<TorrentResult, TorrentSource>> FillMissingHashesAsync(
        ITorrentIndexer indexer,
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
                    var source = await indexer.DownloadAsync(r.DownloadUrl!, token).ConfigureAwait(false);
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
