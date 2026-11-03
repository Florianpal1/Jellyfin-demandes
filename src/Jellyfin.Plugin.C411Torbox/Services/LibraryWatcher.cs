using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Écoute les ajouts dans la bibliothèque pour passer les demandes en « Disponible » sans attendre la tâche planifiée.
/// Les ajouts sont regroupés (un scan ajoute souvent des dizaines d'éléments d'un coup).
/// </summary>
internal sealed class LibraryWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _libraryManager;
    private readonly RequestService _requests;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly Timer _timer;

    public LibraryWatcher(ILibraryManager libraryManager, RequestService requests, ILogger<LibraryWatcher> logger)
    {
        _libraryManager = libraryManager;
        _requests = requests;
        _logger = logger;
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Episode or Book)
        {
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnTimer(object? state)
    {
        try
        {
            await _requests.CheckAvailabilityAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // async void : aucune exception ne doit remonter (elle ferait tomber le serveur).
            _logger.LogError(ex, "Vérification de disponibilité des demandes en échec");
        }
    }
}
