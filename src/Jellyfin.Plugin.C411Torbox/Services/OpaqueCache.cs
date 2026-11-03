using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Cache mémoire à identifiants opaques (aléatoires, 96 bits) : le navigateur ne manipule que l'identifiant,
/// la donnée reste côté serveur. Taille bornée (nombre d'entrées et « poids » total) : au-delà, les plus
/// anciennes entrées sont retirées, pour qu'un utilisateur ne puisse pas saturer la mémoire du serveur.
/// </summary>
public class OpaqueCache<T>
    where T : class
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly int _maxEntries;
    private readonly long _maxWeight;
    private readonly Func<T, long> _weight;
    private long _totalWeight;

    public OpaqueCache(TimeProvider time, TimeSpan lifetime, int maxEntries, long maxWeight = long.MaxValue, Func<T, long>? weight = null)
    {
        _time = time;
        _lifetime = lifetime;
        _maxEntries = maxEntries;
        _maxWeight = maxWeight;
        _weight = weight ?? (_ => 0);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public string Add(T value)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var now = _time.GetUtcNow().UtcDateTime;
        var weight = Math.Max(0, _weight(value));
        lock (_gate)
        {
            Purge(now);
            _entries[id] = new Entry(value, now, now + _lifetime, weight);
            _totalWeight += weight;
            while (_entries.Count > 1 && (_entries.Count > _maxEntries || _totalWeight > _maxWeight))
            {
                Remove(_entries.MinBy(kv => kv.Value.Added).Key);
            }
        }

        return id;
    }

    public T? Get(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var e) && e.Expires > _time.GetUtcNow().UtcDateTime ? e.Value : null;
        }
    }

    private void Purge(DateTime now)
    {
        foreach (var key in _entries.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            Remove(key);
        }
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var e))
        {
            _totalWeight -= e.Weight;
        }
    }

    private sealed record Entry(T Value, DateTime Added, DateTime Expires, long Weight);
}
