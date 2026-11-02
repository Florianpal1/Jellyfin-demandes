using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.C411Torbox;

/// <summary>Enregistre les services du plugin dans l'injection de dépendances de Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Les redirections sont suivies manuellement (certaines mènent à un lien magnet).
        serviceCollection.AddHttpClient(ProwlarrClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        serviceCollection.AddHttpClient(TorboxClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        serviceCollection.AddHttpClient(NotificationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        serviceCollection.AddSingleton<IConfigProvider, PluginConfigProvider>();
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new RequestRepository(Path.Combine(paths.DataPath, "c411torbox", "requests.db"));
        });
        serviceCollection.AddSingleton<SearchResultCache>();
        serviceCollection.AddSingleton<ProwlarrClient>();
        serviceCollection.AddSingleton<TorboxClient>();
        serviceCollection.AddSingleton<ILibraryLookup, LibraryLookup>();
        serviceCollection.AddSingleton<MetadataSearchService>();
        serviceCollection.AddSingleton<TorrentSearchService>();
        serviceCollection.AddSingleton<INotificationService, NotificationService>();
        serviceCollection.AddSingleton<RequestService>();
        serviceCollection.AddHostedService<LibraryWatcher>();

        // TorboxStatusTask (IScheduledTask) est découverte automatiquement par Jellyfin.
    }
}
