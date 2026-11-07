using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.C411Torbox.Tasks;

/// <summary>
/// Tâche planifiée quotidienne : signale aux admins les demandes qui stagnent, puis supprime les demandes terminées
/// et les notifications lues trop anciennes. Durées réglées dans la configuration du plugin.
/// </summary>
public class MaintenanceTask : IScheduledTask
{
    private readonly RequestService _requests;

    public MaintenanceTask(RequestService requests)
    {
        _requests = requests;
    }

    public string Name => "Ménage des demandes";

    public string Key => "C411TorboxMaintenance";

    public string Description => "Signale les demandes en cours depuis trop longtemps et supprime les demandes terminées et les notifications lues anciennes.";

    public string Category => "C411 Torbox";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _requests.CheckStaleAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(50);
        _requests.Purge();
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        };
    }
}
