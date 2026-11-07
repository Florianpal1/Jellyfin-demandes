using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.C411Torbox.Models;

/// <summary>Une demande de téléchargement faite par un utilisateur.</summary>
public class MediaRequest
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public MediaKind MediaKind { get; set; }

    public RequestScope Scope { get; set; }

    public string MediaTitle { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public string? TvdbId { get; set; }

    /// <summary>Identifiants des livres (Google Books, Comic Vine, ISBN), au format « Nom=valeur;Nom=valeur ».</summary>
    public string? BookIds { get; set; }

    public string? PosterUrl { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string TorrentTitle { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int Seeders { get; set; }

    public int Leechers { get; set; }

    public string? InfoHash { get; set; }

    /// <summary>Identifiant de l'indexeur (peut être un lien avec passkey ou clé API : jamais exposé au navigateur).</summary>
    [JsonIgnore]
    public string? ProwlarrGuid { get; set; }

    /// <summary>Lien magnet (peut contenir la passkey : jamais exposé au navigateur).</summary>
    [JsonIgnore]
    public string? MagnetUrl { get; set; }

    /// <summary>Lien de téléchargement Prowlarr (contient la clé API : jamais exposé au navigateur).</summary>
    [JsonIgnore]
    public string? DownloadUrl { get; set; }

    public bool WasCachedOnTorbox { get; set; }

    public AutoApprovalReason AutoApprovalReason { get; set; }

    public RequestStatus Status { get; set; }

    public string? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? RejectReason { get; set; }

    public long? TorboxTorrentId { get; set; }

    /// <summary>Progression Torbox entre 0 et 1.</summary>
    public double? TorboxProgress { get; set; }

    public string? LastError { get; set; }

    /// <summary>Progression du téléchargement vers le NAS entre 0 et 1.</summary>
    public double? ImportProgress { get; set; }

    /// <summary>Dossier de la bibliothèque où les fichiers ont été rangés.</summary>
    public string? ImportedPath { get; set; }

    /// <summary>Pour un pack de saison : épisodes déjà présents au moment de la demande.</summary>
    public int EpisodesInLibraryAtRequest { get; set; }

    /// <summary>Première vérification où le torrent ne figurait plus sur le compte Torbox (null s'il y est).</summary>
    public DateTime? MissingOnTorboxSince { get; set; }

    /// <summary>Tentatives de rangement automatiques déjà faites (remis à zéro par « Relancer »).</summary>
    public int ImportAttempts { get; set; }

    /// <summary>Prochaine tentative de rangement automatique (null : dès que possible).</summary>
    public DateTime? RetryImportAfter { get; set; }

    /// <summary>Date à laquelle les admins ont été prévenus que la demande stagne (null : pas encore).</summary>
    public DateTime? StaleNotifiedAt { get; set; }
}
