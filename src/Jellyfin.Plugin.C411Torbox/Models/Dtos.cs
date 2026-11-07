using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.C411Torbox.Models;

/// <summary>Fiche de média issue des fournisseurs de métadonnées Jellyfin.</summary>
public class MediaSearchResult
{
    /// <summary>Identifiant opaque de la fiche dans le cache serveur (à renvoyer pour la recherche de torrents).</summary>
    public string Id { get; set; } = string.Empty;

    public MediaKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? ImageUrl { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fiche sans fournisseur de métadonnées (livres : recherche du texte saisi tel quel, sans identifiant).
    /// </summary>
    public bool IsFreeText { get; set; }

    /// <summary>Film déjà présent dans la bibliothèque, ou série dont au moins un épisode est présent.</summary>
    public bool InLibrary { get; set; }
}

/// <summary>Recherche de torrents envoyée par le navigateur : la fiche n'est référencée que par son identifiant.</summary>
public class TorrentSearchBody
{
    public string MediaId { get; set; } = string.Empty;

    /// <summary>Numéro de saison (séries uniquement).</summary>
    public int? Season { get; set; }

    /// <summary>Numéro d'épisode (null = pack de saison).</summary>
    public int? Episode { get; set; }
}

/// <summary>Ce que l'utilisateur veut obtenir (construit côté serveur depuis la fiche en cache).</summary>
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

    /// <summary>Recherche pour une fiche du cache serveur : seuls saison et épisode viennent du navigateur.</summary>
    public static TorrentSearchRequest From(MediaSearchResult media, int? season, int? episode) => new()
    {
        Kind = media.Kind,
        Name = media.Name,
        Year = media.Year,
        ImageUrl = media.ImageUrl,
        ProviderIds = new Dictionary<string, string>(media.ProviderIds, StringComparer.OrdinalIgnoreCase),
        Season = season,
        Episode = episode,
    };

    /// <summary>Autres titres du média (ex. titre anglais), complétés par le serveur avant la recherche.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AlternateNames { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public RequestScope Scope => Kind switch
    {
        MediaKind.Movie => RequestScope.Movie,
        MediaKind.Book => RequestScope.Book,
        _ => Episode.HasValue ? RequestScope.Episode : RequestScope.Season,
    };
}

/// <summary>Un torrent trouvé sur C411 (Torznab direct ou via Prowlarr).</summary>
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

    /// <summary>Identifiants du média fournis par l'indexeur (s'il les renvoie).</summary>
    [JsonIgnore]
    public string? TmdbId { get; set; }

    [JsonIgnore]
    public string? ImdbId { get; set; }

    [JsonIgnore]
    public string? TvdbId { get; set; }
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

public class CancelBody
{
    /// <summary>Supprime aussi le torrent de Torbox (si aucune autre demande en cours ne l'utilise).</summary>
    public bool DeleteFromTorbox { get; set; }
}

/// <summary>Un épisode d'une saison, pour le choix de l'épisode à demander.</summary>
public class EpisodeListItem
{
    public int Episode { get; set; }

    /// <summary>Titre (null s'il n'est pas encore connu des fournisseurs de métadonnées).</summary>
    public string? Title { get; set; }

    public bool InLibrary { get; set; }
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
