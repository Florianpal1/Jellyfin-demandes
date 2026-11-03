using System;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Un résultat de recherche conservé côté serveur, avec le contexte de la recherche.</summary>
public sealed record CachedResult(TorrentResult Result, TorrentSearchRequest Context, TorrentSource? Source);

/// <summary>
/// Cache des résultats de recherche. Une demande ne référence qu'un identifiant opaque :
/// taille, seeders, hash et état du cache Torbox sont relus ici, jamais fournis par le navigateur.
/// Les .torrent gardés pour l'envoi à Torbox comptent dans la limite de mémoire.
/// </summary>
public class SearchResultCache : OpaqueCache<CachedResult>
{
    public const int MaxEntries = 5000;

    public const long MaxTorrentBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public SearchResultCache()
        : this(TimeProvider.System)
    {
    }

    public SearchResultCache(TimeProvider time)
        : base(time, Lifetime, MaxEntries, MaxTorrentBytes, e => e.Source?.File?.Length ?? 0)
    {
    }
}

/// <summary>
/// Fiches médias renvoyées par la recherche de métadonnées. La recherche de torrents ne reçoit que
/// l'identifiant de la fiche : titre, année, identifiants TMDB / IMDb et type viennent du serveur.
/// </summary>
public class MediaResultCache : OpaqueCache<MediaSearchResult>
{
    public const int MaxEntries = 5000;

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    public MediaResultCache()
        : this(TimeProvider.System)
    {
    }

    public MediaResultCache(TimeProvider time)
        : base(time, Lifetime, MaxEntries)
    {
    }
}
