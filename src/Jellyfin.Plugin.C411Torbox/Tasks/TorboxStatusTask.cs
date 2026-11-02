using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Tasks;

/// <summary>
/// Tâche planifiée : lit la progression des torrents sur Torbox (lecture seule) puis vérifie
/// l'arrivée des médias dans la bibliothèque. Intervalle modifiable dans Tableau de bord > Tâches planifiées.
/// </summary>
public class TorboxStatusTask : IScheduledTask
{
    private readonly RequestService _requests;
    private readonly ILogger<TorboxStatusTask> _logger;

    public TorboxStatusTask(RequestService requests, ILogger<TorboxStatusTask> logger)
    {
        _requests = requests;
        _logger = logger;
    }

    public string Name => "Suivi des demandes Torbox";

    public string Key => "C411TorboxStatus";

    public string Description => "Met à jour la progression des torrents envoyés à Torbox et détecte leur arrivée dans la bibliothèque.";

    public string Category => "C411 Torbox";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _requests.RefreshTorboxStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TorboxException ex)
        {
            _logger.LogWarning(ex, "Lecture des statuts Torbox impossible");
        }

        progress.Report(50);
        await _requests.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(10).Ticks,
        };
    }
}
