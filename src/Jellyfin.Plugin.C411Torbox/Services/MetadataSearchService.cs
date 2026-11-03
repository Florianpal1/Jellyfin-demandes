using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Recherche de fiches (titre, année, affiche, synopsis, identifiants) via les fournisseurs
/// de métadonnées déjà configurés sur le serveur Jellyfin (TMDB ; Google Books et Comic Vine via l'extension
/// Bookshelf pour les livres) : aucune clé supplémentaire.
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
        IEnumerable<RemoteSearchResult> raw = kind switch
        {
            MediaKind.Movie => await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = query, MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),

            // Fiches fournies par l'extension Bookshelf (Google Books, Comic Vine) si elle est installée.
            MediaKind.Book => await _providerManager.GetRemoteSearchResults<Book, BookInfo>(
                new RemoteSearchQuery<BookInfo>
                {
                    SearchInfo = new BookInfo { Name = query, MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),
            _ => await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = query, MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),
        };

        var results = new List<MediaSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw)
        {
            var source = r.ProviderIds ?? new Dictionary<string, string>();
            var ids = kind == MediaKind.Book ? LibraryLookup.FilterBookIds(source) : LibraryLookup.FilterIds(source);
            var first = ids.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + kv.Value).FirstOrDefault();
            var key = ids.TryGetValue("Tmdb", out var tmdb) ? "tmdb:" + tmdb
                : ids.TryGetValue("Imdb", out var imdb) ? "imdb:" + imdb
                : ids.TryGetValue("Tvdb", out var tvdb) ? "tvdb:" + tvdb
                : first ?? "name:" + r.Name + "|" + r.ProductionYear;
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
                InLibrary = kind switch
                {
                    MediaKind.Movie => _library.IsMovieInLibrary(ids),
                    MediaKind.Book => _library.IsBookInLibrary(ids),
                    _ => _library.IsSeriesInLibrary(ids),
                },
            };
            media.Id = _cache.Add(media);
            results.Add(media);
        }

        // Presse, BD ou éditions absentes de Google Books / Comic Vine : recherche du texte saisi tel quel.
        if (kind == MediaKind.Book)
        {
            var free = new MediaSearchResult
            {
                Kind = kind,
                Name = query,
                Overview = "Recherche sur C411 du texte saisi, sans fiche (journaux, magazines, BD ou éditions introuvables).",
                IsFreeText = true,
            };
            free.Id = _cache.Add(free);
            results.Add(free);
        }

        return results;
    }
}
