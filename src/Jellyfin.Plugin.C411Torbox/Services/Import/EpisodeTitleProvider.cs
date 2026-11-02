using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>Titres des épisodes pour le nommage (facultatif : sans titre, le fichier est nommé « Série - S01E02 »).</summary>
public interface IEpisodeTitleProvider
{
    Task<IReadOnlyDictionary<(int Season, int Episode), string>> GetTitlesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, IEnumerable<(int Season, int Episode)> episodes, CancellationToken ct);
}

/// <summary>Titres lus via les fournisseurs de métadonnées de Jellyfin (TMDB), en français.</summary>
internal sealed class EpisodeTitleProvider : IEpisodeTitleProvider
{
    private static readonly TimeSpan PerEpisodeTimeout = TimeSpan.FromSeconds(15);

    private readonly IProviderManager _providerManager;
    private readonly ILogger<EpisodeTitleProvider> _logger;

    public EpisodeTitleProvider(IProviderManager providerManager, ILogger<EpisodeTitleProvider> logger)
    {
        _providerManager = providerManager;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<(int Season, int Episode), string>> GetTitlesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, IEnumerable<(int Season, int Episode)> episodes, CancellationToken ct)
    {
        var titles = new Dictionary<(int, int), string>();
        foreach (var (season, episode) in episodes.Distinct())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerEpisodeTimeout);
            try
            {
                var info = new EpisodeInfo
                {
                    ParentIndexNumber = season,
                    IndexNumber = episode,
                    MetadataLanguage = "fr",
                    MetadataCountryCode = "FR",
                };
                foreach (var (key, value) in seriesProviderIds)
                {
                    info.SeriesProviderIds[key] = value;
                }

                var results = await _providerManager.GetRemoteSearchResults<Episode, EpisodeInfo>(
                    new RemoteSearchQuery<EpisodeInfo> { SearchInfo = info, IncludeDisabledProviders = true },
                    timeout.Token).ConfigureAwait(false);
                var name = results.Select(r => r.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                if (name is not null && !IsPlaceholder(name, episode))
                {
                    titles[(season, episode)] = name.Trim();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Titre facultatif : on range quand même, sans le titre.
                _logger.LogDebug(ex, "Titre de l'épisode S{Season}E{Episode} introuvable", season, episode);
            }
        }

        return titles;
    }

    /// <summary>TMDB renvoie « Épisode 3 » quand le titre n'est pas encore connu : inutile dans le nom de fichier.</summary>
    internal static bool IsPlaceholder(string name, int episode)
    {
        var n = name.Trim();
        return n.Equals("Épisode " + episode, StringComparison.OrdinalIgnoreCase)
            || n.Equals("Episode " + episode, StringComparison.OrdinalIgnoreCase)
            || n.Equals("TBA", StringComparison.OrdinalIgnoreCase);
    }
}
