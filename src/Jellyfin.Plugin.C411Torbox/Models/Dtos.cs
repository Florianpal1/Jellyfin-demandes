using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.C411Torbox.Models;

/// <summary>Fiche de média issue des fournisseurs de métadonnées Jellyfin.</summary>
public class MediaSearchResult
{
    public MediaKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? ImageUrl { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Film déjà présent dans la bibliothèque, ou série dont au moins un épisode est présent.</summary>
    public bool InLibrary { get; set; }
}

/// <summary>Ce que l'utilisateur veut obtenir (envoyé par le navigateur pour lancer la recherche de torrents).</summary>
public class TorrentSearchRequest
{
    public MediaKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? ImageUrl { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numéro de saison (séries uniquement).</summary>
    public int? Season { get; set; }

    /// <summary>Numéro d'épisode (null = pack de saison).</summary>
    public int? Episode { get; set; }

    [JsonIgnore]
    public RequestScope Scope => Kind == MediaKind.Movie
        ? RequestScope.Movie
        : Episode.HasValue ? RequestScope.Episode : RequestScope.Season;
}

/// <summary>Un torrent trouvé via Prowlarr.</summary>
public class TorrentResult
{
    /// <summary>Identifiant opaque du résultat dans le cache serveur.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int Seeders { get; set; }

    public int Leechers { get; set; }

    public DateTime? PublishDate { get; set; }

    public string? InfoHash { get; set; }

    public bool IsCachedOnTorbox { get; set; }

    /// <summary>Indique si la demande serait validée automatiquement.</summary>
    public bool WouldAutoApprove { get; set; }

    [JsonIgnore]
    public string? Guid { get; set; }

    [JsonIgnore]
    public string? MagnetUrl { get; set; }

    [JsonIgnore]
    public string? DownloadUrl { get; set; }
}

public class TorrentSearchResponse
{
    public RequestScope Scope { get; set; }

    /// <summary>Le média demandé est déjà dans la bibliothèque : les demandes sont bloquées.</summary>
    public bool AlreadyInLibrary { get; set; }

    /// <summary>Pour un pack de saison : nombre d'épisodes déjà présents.</summary>
    public int EpisodesInLibrary { get; set; }

    public IReadOnlyList<TorrentResult> Results { get; set; } = Array.Empty<TorrentResult>();

    /// <summary>Avertissement éventuel (ex. hash non calculé pour certains résultats).</summary>
    public string? Warning { get; set; }
}

public class CreateRequestBody
{
    public string ResultId { get; set; } = string.Empty;
}

public class RejectBody
{
    public string? Reason { get; set; }
}

public class Notification
{
    public long Id { get; set; }

    /// <summary>Destinataire ; null = tous les administrateurs.</summary>
    public Guid? UserId { get; set; }

    public long? RequestId { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}

public class CurrentUserInfo
{
    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }
}

public class ConnectionTestResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}

public class ProwlarrIndexerInfo
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}
