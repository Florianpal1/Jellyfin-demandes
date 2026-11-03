using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Règles d'auto-validation (PLAN.md § 2.5).</summary>
public static class ApprovalRules
{
    private const double BytesPerGb = 1024d * 1024 * 1024;

    /// <summary>
    /// 1. Déjà en cache Torbox (si activé) ET taille ≤ seuil du type (seuil 0 : pas de borne) → auto-validé.
    /// 2. Taille ≤ seuil du type ET seeders ≥ minimum → auto-validé.
    /// 3. Sinon → en attente.
    /// </summary>
    public static AutoApprovalReason Evaluate(
        RequestScope scope,
        long sizeBytes,
        int seeders,
        bool cachedOnTorbox,
        PluginConfiguration config)
    {
        double maxGb = scope switch
        {
            RequestScope.Movie => config.MovieMaxSizeGb,
            RequestScope.Episode => config.EpisodeMaxSizeGb,
            RequestScope.Season => config.SeasonMaxSizeGb,
            _ => 0,
        };

        bool underMaxSize = maxGb > 0 && sizeBytes > 0 && sizeBytes <= maxGb * BytesPerGb;

        // Le seuil borne aussi le cache (un torrent en cache est quand même téléchargé sur le NAS).
        // Seuil à 0 : la règle « taille » est désactivée, le cache reste alors sans borne.
        if (cachedOnTorbox && config.AutoApproveWhenCached && (maxGb <= 0 || underMaxSize))
        {
            return AutoApprovalReason.Cached;
        }

        if (underMaxSize && seeders >= config.MinSeedersForAutoApproval)
        {
            return AutoApprovalReason.Size;
        }

        return AutoApprovalReason.None;
    }
}
