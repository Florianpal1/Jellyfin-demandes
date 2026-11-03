using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Recherche de fiches (titre, année, affiche, synopsis, identifiants) via les fournisseurs
/// de métadonnées déjà configurés sur le serveur Jellyfin (TMDB) : aucune clé supplémentaire.
/// </summary>
public class MetadataSearchService
{
    private readonly IProviderManager _providerManager;
    private readonly ILibraryLookup _library;
    private readonly MediaResultCache _cache;

    public MetadataSearchService(IProviderManager providerManager, ILibraryLookup library, MediaResultCache cache)
    {
        _providerManager = providerManager;
        _library = library;
        _cache = cache;
    }

    /// <summary>Fiche renvoyée par une recherche récente (null si inconnue ou expirée).</summary>
    public MediaSearchResult? Get(string id) => _cache.Get(id);

    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, MediaKind kind, CancellationToken ct)
    {
        IEnumerable<RemoteSearchResult> raw = kind == MediaKind.Movie
            ? await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = query, MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false)
            : await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = query, MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false);

        var results = new List<MediaSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw)
        {
            var ids = LibraryLookup.FilterIds(r.ProviderIds ?? new Dictionary<string, string>());
            var key = ids.TryGetValue("Tmdb", out var tmdb) ? "tmdb:" + tmdb
                : ids.TryGetValue("Imdb", out var imdb) ? "imdb:" + imdb
                : ids.TryGetValue("Tvdb", out var tvdb) ? "tvdb:" + tvdb
                : "name:" + r.Name + "|" + r.ProductionYear;
            if (!seen.Add(key))
            {
                continue;
            }

            var media = new MediaSearchResult
            {
                Kind = kind,
                Name = r.Name ?? string.Empty,
                Year = r.ProductionYear,
                Overview = r.Overview,
                ImageUrl = r.ImageUrl,
                ProviderIds = ids,
                InLibrary = kind == MediaKind.Movie ? _library.IsMovieInLibrary(ids) : _library.IsSeriesInLibrary(ids),
            };
            media.Id = _cache.Add(media);
            results.Add(media);
        }

        return results;
    }
}
