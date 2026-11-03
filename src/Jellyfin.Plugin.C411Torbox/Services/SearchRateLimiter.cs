using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Limite le nombre de recherches de torrents par utilisateur (fenêtre glissante) : chaque recherche
/// interroge C411 et télécharge des .torrent, ce qui consomme le quota de la clé API.
/// </summary>
public class SearchRateLimiter
{
    public const int MaxPerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, Queue<DateTime>> _hits = new();
    private readonly TimeProvider _time;

    public SearchRateLimiter()
        : this(TimeProvider.System)
    {
    }

    public SearchRateLimiter(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Compte une recherche ; false si l'utilisateur a atteint la limite.</summary>
    public bool TryAcquire(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_hits)
        {
            if (!_hits.TryGetValue(userId, out var queue))
            {
                queue = new Queue<DateTime>();
                _hits[userId] = queue;
            }

            while (queue.Count > 0 && queue.Peek() <= now - Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxPerWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
