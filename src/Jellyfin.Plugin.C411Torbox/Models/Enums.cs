using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.C411Torbox.Models;

[JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>Portée d'une demande : détermine le seuil de taille appliqué.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestScope>))]
public enum RequestScope
{
    Movie,
    Episode,
    Season,
}

[JsonConverter(typeof(JsonStringEnumConverter<RequestStatus>))]
public enum RequestStatus
{
    /// <summary>En attente de validation admin.</summary>
    Pending,

    /// <summary>Validée par un admin, envoi Torbox en cours.</summary>
    Approved,

    /// <summary>Validée automatiquement, envoi Torbox en cours.</summary>
    AutoApproved,

    Rejected,

    SentToTorbox,

    DownloadingOnTorbox,

    CompletedOnTorbox,

    /// <summary>Téléchargement des fichiers de Torbox vers le NAS en cours.</summary>
    Importing,

    /// <summary>Fichiers téléchargés et rangés dans la bibliothèque, en attente du scan Jellyfin.</summary>
    Imported,

    /// <summary>Le téléchargement ou le rangement a échoué (relance possible par un admin).</summary>
    ImportFailed,

    /// <summary>Le média est apparu dans la bibliothèque Jellyfin.</summary>
    Available,

    /// <summary>L'envoi à Torbox a échoué (renvoi manuel possible).</summary>
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<AutoApprovalReason>))]
public enum AutoApprovalReason
{
    None,
    Cached,
    Size,
}
