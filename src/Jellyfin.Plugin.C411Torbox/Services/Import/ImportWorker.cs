using System;
using System.Collections.Generic;
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
/// (toutes les 2 min, sans attendre la tâche planifiée), puis télécharge et range les torrents terminés,
/// plusieurs à la fois selon le réglage « téléchargements simultanés ».
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
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
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

                    // Plusieurs torrents à la fois ; la limite globale de fichiers est appliquée par ImportService.
                    while (running.Count < _import.ParallelDownloads
                        && await _import.TryStartNextAsync(stoppingToken).ConfigureAwait(false) is { } import)
                    {
                        running.Add(import);
                    }
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

            // Réveil dès qu'un import se termine (place libérée), sinon après le délai habituel.
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                await Task.WhenAny(running.Append(Task.Delay(IdleDelay, idle.Token))).ConfigureAwait(false);
                idle.Cancel();
            }

            Collect(running);
        }

        // Arrêt du serveur : chaque import remet ses demandes en attente avant de rendre la main.
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Retire les imports terminés ; une erreur non gérée par l'import lui-même est journalisée.</summary>
    private void Collect(List<Task> running)
    {
        foreach (var task in running.Where(t => t.IsCompleted).ToList())
        {
            running.Remove(task);
            if (task.IsFaulted && task.Exception!.InnerException is not OperationCanceledException)
            {
                _logger.LogError(task.Exception.InnerException, "Import automatique : erreur inattendue");
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
