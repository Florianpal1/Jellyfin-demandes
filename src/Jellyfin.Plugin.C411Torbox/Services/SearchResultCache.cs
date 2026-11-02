using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Un résultat de recherche conservé côté serveur, avec le contexte de la recherche.</summary>
public sealed record CachedResult(TorrentResult Result, TorrentSearchRequest Context, TorrentSource? Source);

/// <summary>
/// Cache des résultats de recherche. Une demande ne référence qu'un identifiant opaque :
/// taille, seeders, hash et état du cache Torbox sont relus ici, jamais fournis par le navigateur.
/// </summary>
public class SearchResultCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (CachedResult Entry, DateTime Expires)> _entries = new();
    private readonly TimeProvider _time;

    public SearchResultCache()
        : this(TimeProvider.System)
    {
    }

    public SearchResultCache(TimeProvider time)
    {
        _time = time;
    }

    public string Add(CachedResult entry)
    {
        Purge();
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _entries[id] = (entry, _time.GetUtcNow().UtcDateTime + Lifetime);
        return id;
    }

    public CachedResult? Get(string id)
    {
        if (_entries.TryGetValue(id, out var e) && e.Expires > _time.GetUtcNow().UtcDateTime)
        {
            return e.Entry;
        }

        return null;
    }

    private void Purge()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var kv in _entries)
        {
            if (kv.Value.Expires <= now)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
