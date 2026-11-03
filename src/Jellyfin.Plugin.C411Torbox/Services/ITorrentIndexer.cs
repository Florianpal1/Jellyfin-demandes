using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Source des torrents : C411 en direct (Torznab) ou via Prowlarr.</summary>
public interface ITorrentIndexer
{
    /// <summary>Nom affiché dans les messages d'erreur.</summary>
    string DisplayName { get; }

    /// <summary>Recherche les torrents d'un média (par identifiant si possible, sinon par titre).</summary>
    Task<IReadOnlyList<TorrentResult>> SearchAsync(TorrentSearchRequest request, CancellationToken ct);

    /// <summary>Télécharge le .torrent (ou suit la redirection vers un lien magnet).</summary>
    Task<TorrentSource> DownloadAsync(string downloadUrl, CancellationToken ct);

    /// <summary>Vérifie la configuration ; renvoie un message de réussite.</summary>
    Task<string> TestAsync(CancellationToken ct);
}

/// <summary>Donne la source configurée (lue à chaque appel : un changement de config s'applique aussitôt).</summary>
public class IndexerProvider
{
    private readonly ProwlarrClient _prowlarr;
    private readonly TorznabClient _torznab;
    private readonly IConfigProvider _config;

    public IndexerProvider(ProwlarrClient prowlarr, TorznabClient torznab, IConfigProvider config)
    {
        _prowlarr = prowlarr;
        _torznab = torznab;
        _config = config;
    }

    public ITorrentIndexer Current => _config.Current.IndexerMode == IndexerMode.Prowlarr ? _prowlarr : _torznab;
}

/// <summary>Téléchargement des fichiers .torrent, commun aux deux sources.</summary>
internal static class TorrentFileDownloader
{
    public const string HttpClientName = "C411Torbox.Indexer";

    private const int MaxRedirects = 5;
    private const long MaxTorrentFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Certains indexeurs redirigent vers un lien magnet : les redirections sont suivies manuellement
    /// (le client HTTP est configuré sans redirection automatique).
    /// </summary>
    public static async Task<TorrentSource> DownloadAsync(
        HttpClient client,
        string downloadUrl,
        string sourceName,
        CertificateDiagnostics? certificates,
        Action<HttpRequestMessage>? configure,
        CancellationToken ct)
    {
        var origin = new Uri(downloadUrl);
        var url = origin;
        for (int i = 0; i <= MaxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // En-têtes d'authentification (clé API Prowlarr) : jamais transmis à un autre serveur après une redirection.
            if (SameOrigin(url, origin))
            {
                configure?.Invoke(request);
            }
            using var response = await SendAsync(client, request, HttpCompletionOption.ResponseHeadersRead, sourceName, certificates, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location
                    ?? throw new HttpRequestException(sourceName + " : redirection sans destination.");
                var target = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (target.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                {
                    return new TorrentSource(null, target.OriginalString);
                }

                url = target;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Téléchargement du .torrent refusé par {sourceName} (HTTP {(int)response.StatusCode})."));
            }

            if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
            {
                throw new HttpRequestException("Fichier .torrent trop volumineux.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > MaxTorrentFileBytes)
            {
                throw new HttpRequestException("Fichier .torrent trop volumineux.");
            }

            var text = bytes.Length < 4096 ? Encoding.UTF8.GetString(bytes).Trim() : string.Empty;
            if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                return new TorrentSource(null, text);
            }

            return new TorrentSource(bytes, null);
        }

        throw new HttpRequestException("Trop de redirections lors du téléchargement du .torrent.");
    }

    /// <summary>Même schéma, même hôte et même port (pas de passage de https à http).</summary>
    internal static bool SameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;

    /// <summary>Envoie la requête en remplaçant les erreurs réseau par un message lisible.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption option,
        string sourceName,
        CertificateDiagnostics? certificates,
        CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, option, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(sourceName + " injoignable : " + NetworkErrors.Describe(ex, certificates?.Get(request.RequestUri?.Host)), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(sourceName + " injoignable : " + NetworkErrors.Describe(ex), ex);
        }
    }
}
