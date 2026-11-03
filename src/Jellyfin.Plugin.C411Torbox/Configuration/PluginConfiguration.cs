using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.C411Torbox.Configuration;

/// <summary>
/// Configuration du plugin (éditée par les administrateurs).
/// Les clés API restent côté serveur : elles ne sont jamais renvoyées par les endpoints du plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Source des torrents : C411 en direct (Torznab, par défaut) ou via Prowlarr.</summary>
    public IndexerMode IndexerMode { get; set; } = IndexerMode.Torznab;

    /// <summary>URL de l'API Torznab de C411 (celle saisie dans Radarr/Sonarr).</summary>
    public string TorznabUrl { get; set; } = "https://c411.org/api";

    /// <summary>Clé API C411 (celle saisie dans Radarr/Sonarr).</summary>
    public string TorznabApiKey { get; set; } = string.Empty;

    /// <summary>URL de Prowlarr, ex. http://prowlarr:9696.</summary>
    public string ProwlarrUrl { get; set; } = string.Empty;

    public string ProwlarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Accepte un certificat HTTPS non valide (auto-signé) pour la source des torrents.
    /// À n'activer que pour un Prowlarr du réseau local.
    /// </summary>
    public bool ProwlarrIgnoreCertificateErrors { get; set; }

    /// <summary>Identifiant de l'indexeur C411 dans Prowlarr (0 = non configuré).</summary>
    public int ProwlarrIndexerId { get; set; }

    /// <summary>
    /// Catégories des films, séparées par des virgules.
    /// Chez C411 (t=caps) : 2030 Film, 2060 Animation, 2070 Documentaire, 2000 Films &amp; Vidéos.
    /// </summary>
    public string MovieCategories { get; set; } = "2000,2030,2060,2070";

    /// <summary>
    /// Catégories des séries, séparées par des virgules.
    /// Chez C411 (t=caps) : 5000 Série TV, 5070 Animation Série, 5080 Émission TV.
    /// </summary>
    public string TvCategories { get; set; } = "5000,5070,5080";

    /// <summary>
    /// Catégories des livres, BD, comics et presse, séparées par des virgules.
    /// Newznab : 7000 Livres, 7010 Magazines, 7020 E-books, 7030 Comics.
    /// </summary>
    public string BookCategories { get; set; } = "7000,7010,7020,7030";

    public string TorboxApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Télécharge les torrents terminés sur Torbox vers le NAS et les range dans la bibliothèque
    /// (remplace l'application « RealDebrid » + Radarr / Sonarr). Désactivé par défaut pour ne pas
    /// doublonner une récupération déjà en place.
    /// </summary>
    public bool AutoImportEnabled { get; set; }

    /// <summary>Dossier des films (vide : premier dossier de la bibliothèque Jellyfin de type Films).</summary>
    public string MoviesPath { get; set; } = string.Empty;

    /// <summary>Dossier des séries (vide : premier dossier de la bibliothèque Jellyfin de type Séries).</summary>
    public string SeriesPath { get; set; } = string.Empty;

    /// <summary>Dossier des livres (vide : premier dossier de la bibliothèque Jellyfin de type Livres).</summary>
    public string BooksPath { get; set; } = string.Empty;

    /// <summary>Supprime le torrent de Torbox une fois les fichiers rangés (libère un emplacement Torbox).</summary>
    public bool DeleteFromTorboxAfterImport { get; set; }

    /// <summary>Copie aussi les sous-titres présents dans le torrent.</summary>
    public bool ImportSubtitles { get; set; } = true;

    /// <summary>Auto-validation si le torrent est déjà en cache sur Torbox (dans la limite de la taille max du type, si elle est définie).</summary>
    public bool AutoApproveWhenCached { get; set; } = true;

    /// <summary>Taille max (Go) d'un film pour l'auto-validation. 0 = désactivé.</summary>
    public double MovieMaxSizeGb { get; set; } = 15;

    /// <summary>Taille max (Go) d'un épisode pour l'auto-validation. 0 = désactivé.</summary>
    public double EpisodeMaxSizeGb { get; set; } = 5;

    /// <summary>Taille max (Go) d'un pack de saison pour l'auto-validation. 0 = désactivé.</summary>
    public double SeasonMaxSizeGb { get; set; } = 30;

    /// <summary>Taille max (Go) d'un livre, d'une BD ou d'un pack pour l'auto-validation. 0 = désactivé.</summary>
    public double BookMaxSizeGb { get; set; } = 2;

    /// <summary>Nombre minimum de seeders pour l'auto-validation par la taille.</summary>
    public int MinSeedersForAutoApproval { get; set; } = 5;

    /// <summary>
    /// Nombre max de résultats pour lesquels le .torrent est téléchargé afin de calculer l'infohash
    /// quand Prowlarr ne le fournit pas (chaque téléchargement peut compter sur C411).
    /// </summary>
    public int HashLookupLimit { get; set; } = 15;

    /// <summary>Webhook Discord optionnel pour les notifications.</summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;
}
