using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>
/// Tâche de fond de l'import automatique : suit Torbox de près tant que des demandes y sont en cours
/// (toutes les 2 min, sans attendre la tâche planifiée), puis télécharge et range les torrents terminés.
/// </summary>
internal sealed class ImportWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TorboxRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly ImportService _import;
    private readonly RequestService _requests;
    private readonly RequestRepository _repository;
    private readonly ILogger<ImportWorker> _logger;

    public ImportWorker(ImportService import, RequestService requests, RequestRepository repository, ILogger<ImportWorker> logger)
    {
        _import = import;
        _requests = requests;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _requests.ResetInterruptedImportsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reprise des imports interrompus impossible");
        }

        var lastTorboxRefresh = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = false;
            try
            {
                if (_import.Enabled)
                {
                    if (DateTime.UtcNow - lastTorboxRefresh >= TorboxRefreshInterval
                        && _repository.GetActive().Any(r => r.Status is RequestStatus.SentToTorbox or RequestStatus.DownloadingOnTorbox))
                    {
                        lastTorboxRefresh = DateTime.UtcNow;
                        await _requests.RefreshTorboxStatusAsync(stoppingToken).ConfigureAwait(false);
                    }

                    didWork = await _import.ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Une erreur inattendue ne doit jamais arrêter la boucle (ni le serveur).
                _logger.LogError(ex, "Import automatique : erreur inattendue");
            }

            if (!didWork)
            {
                try
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>Scan ciblé du dossier modifié, via la surveillance de bibliothèque de Jellyfin.</summary>
internal sealed class LibraryRefresher : ILibraryRefresher
{
    private readonly ILibraryMonitor _monitor;
    private readonly ILogger<LibraryRefresher> _logger;

    public LibraryRefresher(ILibraryMonitor monitor, ILogger<LibraryRefresher> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public void Refresh(string path)
    {
        try
        {
            _monitor.ReportFileSystemChanged(path);
        }
        catch (Exception ex)
        {
            // Le scan planifié de Jellyfin finira par trouver les fichiers.
            _logger.LogWarning(ex, "Signalement du dossier {Path} à Jellyfin impossible", path);
        }
    }
}
