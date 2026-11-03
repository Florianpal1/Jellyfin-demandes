using System;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Net.Security;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Services;
using Jellyfin.Plugin.C411Torbox.Services.Import;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox;

/// <summary>Enregistre les services du plugin dans l'injection de dépendances de Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Client de la source des torrents (C411 Torznab ou Prowlarr). Les redirections sont suivies
        // manuellement (certaines mènent à un lien magnet).
        serviceCollection.AddHttpClient(ProwlarrClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var config = sp.GetRequiredService<IConfigProvider>();
                var diagnostics = sp.GetRequiredService<CertificateDiagnostics>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("C411Torbox.Indexer");
                return new HttpClientHandler
                {
                    AllowAutoRedirect = false,

                    // Option lue à chaque connexion : la cocher/décocher s'applique sans redémarrage.
                    ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                    {
                        if (errors == SslPolicyErrors.None)
                        {
                            return true;
                        }

                        // Diagnostic : raison exacte du refus (autorité inconnue du système, chaîne incomplète, nom…).
                        var details = chain is null
                            ? string.Empty
                            : string.Join(" ; ", chain.ChainStatus.Select(s => s.Status + " : " + s.StatusInformation.Trim()));
                        var reason = CertificateDiagnostics.Explain(errors, chain?.ChainStatus.Select(s => s.Status) ?? []);
                        var host = request.RequestUri?.Host;
                        diagnostics.Record(host, reason + " " + CertificateDiagnostics.DescribeContext(host, cert));
                        logger.LogWarning(
                            "Certificat HTTPS refusé pour {Host} : {Reason}. Erreurs : {Errors}. Sujet : {Subject}. Émetteur : {Issuer}. Chaîne : {Chain}",
                            request.RequestUri?.Host,
                            reason,
                            errors,
                            cert?.Subject,
                            cert?.Issuer,
                            details);
                        // Jamais pour C411 en direct (site public avec un vrai certificat).
                        return config.Current.IndexerMode == Configuration.IndexerMode.Prowlarr
                            && config.Current.ProwlarrIgnoreCertificateErrors;
                    },
                };
            });
        serviceCollection.AddHttpClient(TorboxClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        serviceCollection.AddHttpClient(NotificationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        // Fichiers de plusieurs Go : pas de délai global, l'arrêt passe par le jeton d'annulation.
        serviceCollection.AddHttpClient(FileDownloader.HttpClientName, c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);

        serviceCollection.AddSingleton<IConfigProvider, PluginConfigProvider>();
        serviceCollection.AddSingleton<CertificateDiagnostics>();
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new RequestRepository(Path.Combine(paths.DataPath, "c411torbox", "requests.db"));
        });
        serviceCollection.AddSingleton<SearchResultCache>();
        serviceCollection.AddSingleton<MediaResultCache>();
        serviceCollection.AddSingleton<SearchRateLimiter>();
        serviceCollection.AddSingleton<ProwlarrClient>();
        serviceCollection.AddSingleton<TorznabClient>();
        serviceCollection.AddSingleton<IndexerProvider>();
        serviceCollection.AddSingleton<TorboxClient>();
        serviceCollection.AddSingleton<ILibraryLookup, LibraryLookup>();
        serviceCollection.AddSingleton<MetadataSearchService>();
        serviceCollection.AddSingleton<ITitleAliasProvider>(sp => sp.GetRequiredService<MetadataSearchService>());
        serviceCollection.AddSingleton<TorrentSearchService>();
        serviceCollection.AddSingleton<INotificationService, NotificationService>();
        serviceCollection.AddSingleton<RequestService>();
        serviceCollection.AddHostedService<LibraryWatcher>();

        // Import automatique (téléchargement Torbox → NAS et rangement façon Radarr / Sonarr).
        serviceCollection.AddSingleton<FileDownloader>();
        serviceCollection.AddSingleton<IEpisodeTitleProvider, EpisodeTitleProvider>();
        serviceCollection.AddSingleton<ILibraryRefresher, LibraryRefresher>();
        serviceCollection.AddSingleton<ImportService>();
        serviceCollection.AddHostedService<ImportWorker>();

        // TorboxStatusTask (IScheduledTask) est découverte automatiquement par Jellyfin.
    }
}
