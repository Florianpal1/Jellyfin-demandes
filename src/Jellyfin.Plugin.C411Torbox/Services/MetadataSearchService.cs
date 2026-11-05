using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
public partial class MetadataSearchService : ITitleAliasProvider
{
    /// <summary>Nom du fournisseur TMDB intégré à Jellyfin.</summary>
    private const string TmdbProviderName = "TheMovieDb";

    private static readonly Dictionary<string, string> Empty = new();

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

    /// <summary>
    /// Titre anglais du média (les fiches sont en français, les releases portent souvent le titre anglais),
    /// lu par identifiant auprès des fournisseurs du serveur.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAlternateNamesAsync(TorrentSearchRequest request, CancellationToken ct)
    {
        // Livres : pas de titre traduit fiable chez les fournisseurs.
        var ids = LibraryLookup.FilterIds(request.ProviderIds);
        if (ids.Count == 0 || request.Kind is not (MediaKind.Movie or MediaKind.Series))
        {
            return Array.Empty<string>();
        }

        IEnumerable<RemoteSearchResult> raw = request.Kind == MediaKind.Movie
            ? await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = request.Name, Year = request.Year, ProviderIds = new(ids), MetadataLanguage = "en", MetadataCountryCode = "US" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false)
            : await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = request.Name, Year = request.Year, ProviderIds = new(ids), MetadataLanguage = "en", MetadataCountryCode = "US" },
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false);

        // Seules les fiches du même média (même identifiant) comptent.
        return raw
            .Where(r => r.ProviderIds is not null && ids.Any(kv => r.ProviderIds.TryGetValue(kv.Key, out var v)
                && string.Equals(v?.Trim(), kv.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Name?.Trim())
            .OfType<string>()
            .Where(n => n.Length > 0 && !n.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Recherche de fiches par titre, limitée à l'année <paramref name="year"/> si elle est renseignée,
    /// ou par identifiant si la saisie en est un (voir <see cref="ParseIdQuery"/>).
    /// </summary>
    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, MediaKind kind, int? year, CancellationToken ct)
    {
        // Identifiant ou lien de fiche : le titre exact, quelle que soit l'année. Un lien TMDB précise lui-même film ou série.
        Dictionary<string, string>? searchIds = null;
        if (kind != MediaKind.Book && ParseIdQuery(query, kind) is { } idQuery)
        {
            kind = idQuery.Kind;
            searchIds = new(StringComparer.OrdinalIgnoreCase) { [idQuery.Provider] = idQuery.Value };
            query = string.Empty;
            year = null;
        }

        // Films et séries : TMDB seul. Une recherche par nom n'y renvoie que l'identifiant Tmdb, et OMDb / TVDB
        // que leur propre identifiant : Jellyfin ne peut pas rapprocher les fiches et le même titre sortirait
        // deux fois (en français puis en anglais). Les autres fournisseurs ne servent que si TMDB ne trouve rien.
        var raw = kind == MediaKind.Book
            ? await QueryProvidersAsync(query, kind, year, searchIds, null, ct).ConfigureAwait(false)
            : await QueryProvidersAsync(query, kind, year, searchIds, TmdbProviderName, ct).ConfigureAwait(false);
        if (raw.Count == 0 && kind != MediaKind.Book)
        {
            raw = await QueryProvidersAsync(query, kind, year, searchIds, null, ct).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Identifiant saisi à la place d'un titre : lien TMDB ou IMDb, identifiant IMDb (« tt0068646 »), ou préfixé
    /// (« tmdb:238 », « imdb:tt0068646 », « tvdb:81189 »). Un nombre seul reste un titre (« 1917 », « 300 »).
    /// </summary>
    internal static (MediaKind Kind, string Provider, string Value)? ParseIdQuery(string query, MediaKind kind)
    {
        var q = query.Trim();
        var tmdbLink = TmdbLinkRegex().Match(q);
        if (tmdbLink.Success)
        {
            return (tmdbLink.Groups[1].Value.Equals("tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Series : MediaKind.Movie, "Tmdb", tmdbLink.Groups[2].Value);
        }

        var prefixed = PrefixedIdRegex().Match(q);
        if (prefixed.Success)
        {
            var value = prefixed.Groups[2].Value.ToLowerInvariant();
            var isImdb = prefixed.Groups[1].Value.Equals("imdb", StringComparison.OrdinalIgnoreCase);

            // IMDb : « tt » suivi de chiffres ; TMDB et TVDB : chiffres seuls.
            if (isImdb != value.StartsWith("tt", StringComparison.Ordinal))
            {
                return null;
            }

            var provider = isImdb ? "Imdb" : prefixed.Groups[1].Value.Equals("tmdb", StringComparison.OrdinalIgnoreCase) ? "Tmdb" : "Tvdb";
            return (kind, provider, value);
        }

        var imdb = ImdbIdRegex().Match(q);
        return imdb.Success && (q.Equals(imdb.Value, StringComparison.OrdinalIgnoreCase) || q.Contains("imdb.com", StringComparison.OrdinalIgnoreCase))
            ? (kind, "Imdb", imdb.Value.ToLowerInvariant())
            : null;
    }

    [GeneratedRegex(@"themoviedb\.org/(?:[a-z]{2}(?:-[A-Z]{2})?/)?(movie|tv)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbLinkRegex();

    [GeneratedRegex(@"^(tmdb|tvdb|imdb)\s*:\s*(tt\d{7,10}|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedIdRegex();

    [GeneratedRegex(@"\btt\d{7,10}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdRegex();

    /// <summary>
    /// Recherche par nom (ou par identifiants s'ils sont renseignés) auprès des fournisseurs du serveur,
    /// ou du seul <paramref name="providerName"/> s'il est renseigné.
    /// </summary>
    private async Task<IReadOnlyList<RemoteSearchResult>> QueryProvidersAsync(
        string query, MediaKind kind, int? year, IReadOnlyDictionary<string, string>? ids, string? providerName, CancellationToken ct)
    {
        IEnumerable<RemoteSearchResult> raw = kind switch
        {
            MediaKind.Movie => await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = query, Year = year, ProviderIds = new(ids ?? Empty, StringComparer.OrdinalIgnoreCase), MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    SearchProviderName = providerName,
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),

            // Fiches fournies par l'extension Bookshelf (Google Books, Comic Vine) si elle est installée.
            MediaKind.Book => await _providerManager.GetRemoteSearchResults<Book, BookInfo>(
                new RemoteSearchQuery<BookInfo>
                {
                    SearchInfo = new BookInfo { Name = query, Year = year, ProviderIds = new(ids ?? Empty, StringComparer.OrdinalIgnoreCase), MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    SearchProviderName = providerName,
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),
            _ => await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = query, Year = year, ProviderIds = new(ids ?? Empty, StringComparer.OrdinalIgnoreCase), MetadataLanguage = "fr", MetadataCountryCode = "FR" },
                    SearchProviderName = providerName,
                    IncludeDisabledProviders = true,
                },
                ct).ConfigureAwait(false),
        };

        // TMDB ne restreint que les films à l'année demandée : le filtre vaut pour tous les fournisseurs.
        return raw.Where(r => year is null || r.ProductionYear == year).ToList();
    }
}
