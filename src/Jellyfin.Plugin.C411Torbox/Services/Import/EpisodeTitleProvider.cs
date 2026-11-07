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

    /// <summary>
    /// Épisodes connus d'une saison (numéro et titre, titre null s'il n'est pas encore connu), dans l'ordre.
    /// Liste vide si les fournisseurs de métadonnées ne connaissent pas la saison.
    /// </summary>
    Task<IReadOnlyList<(int Episode, string? Title)>> GetSeasonEpisodesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken ct);
}

/// <summary>Titres lus via les fournisseurs de métadonnées de Jellyfin (TMDB), en français.</summary>
internal sealed class EpisodeTitleProvider : IEpisodeTitleProvider
{
    private static readonly TimeSpan PerEpisodeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Garde-fou : nombre max d'épisodes lus pour une saison (émissions quotidiennes, animés…).</summary>
    private const int MaxEpisodesPerSeason = 200;

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
            var (_, name) = await LookupAsync(seriesProviderIds, season, episode, ct).ConfigureAwait(false);
            if (name is not null)
            {
                titles[(season, episode)] = name;
            }
        }

        return titles;
    }

    public async Task<IReadOnlyList<(int Episode, string? Title)>> GetSeasonEpisodesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken ct)
    {
        // Les fournisseurs n'exposent pas la liste d'une saison : on lit les épisodes un par un jusqu'au premier absent.
        // TMDB met la saison en cache après le premier épisode : les suivants sont immédiats.
        var episodes = new List<(int, string?)>();
        for (int episode = 1; episode <= MaxEpisodesPerSeason; episode++)
        {
            var (found, name) = await LookupAsync(seriesProviderIds, season, episode, ct).ConfigureAwait(false);
            if (!found)
            {
                break;
            }

            episodes.Add((episode, name));
        }

        return episodes;
    }

    /// <summary>Épisode connu des fournisseurs, et son titre (null s'il n'est pas encore connu).</summary>
    private async Task<(bool Found, string? Title)> LookupAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken ct)
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

            var results = (await _providerManager.GetRemoteSearchResults<Episode, EpisodeInfo>(
                new RemoteSearchQuery<EpisodeInfo> { SearchInfo = info, IncludeDisabledProviders = true },
                timeout.Token).ConfigureAwait(false)).ToList();
            var name = results.Select(r => r.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            return (results.Count > 0, name is not null && !IsPlaceholder(name, episode) ? name.Trim() : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Titre facultatif : on range quand même, sans le titre.
            _logger.LogDebug(ex, "Épisode S{Season}E{Episode} introuvable", season, episode);
            return (false, null);
        }
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
