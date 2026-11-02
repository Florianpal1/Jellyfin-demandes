using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Un torrent à envoyer : soit un fichier .torrent, soit un lien magnet.</summary>
public sealed record TorrentSource(byte[]? File, string? Magnet);

/// <summary>Paramètres d'une recherche Prowlarr.</summary>
public sealed record ProwlarrQuery(string Query, string Type, IReadOnlyList<int> Categories);

/// <summary>Client de l'API v1 de Prowlarr (C411 est un indexeur de Prowlarr).</summary>
public class ProwlarrClient
{
    public const string HttpClientName = "C411Torbox.Prowlarr";

    private const int MaxRedirects = 5;
    private const long MaxTorrentFileBytes = 10 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _config;
    private readonly CertificateDiagnostics? _certificates;

    public ProwlarrClient(IHttpClientFactory httpClientFactory, IConfigProvider config, CertificateDiagnostics? certificates = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _certificates = certificates;
    }

    public async Task<IReadOnlyList<ProwlarrIndexerInfo>> GetIndexersAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("api/v1/indexer", ct).ConfigureAwait(false);
        var list = new List<ProwlarrIndexerInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            list.Add(new ProwlarrIndexerInfo
            {
                Id = item.GetInt("id"),
                Name = item.GetStringOrNull("name") ?? string.Empty,
                Enabled = item.TryGetProperty("enable", out var e) && e.ValueKind == JsonValueKind.True,
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(ProwlarrQuery query, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (cfg.ProwlarrIndexerId <= 0)
        {
            throw new InvalidOperationException("L'indexeur C411 n'est pas configuré dans le plugin.");
        }

        var sb = new StringBuilder("api/v1/search?limit=100");
        sb.Append("&query=").Append(Uri.EscapeDataString(query.Query));
        sb.Append("&type=").Append(Uri.EscapeDataString(query.Type));
        sb.Append("&indexerIds=").Append(cfg.ProwlarrIndexerId.ToString(CultureInfo.InvariantCulture));
        foreach (var cat in query.Categories)
        {
            sb.Append("&categories=").Append(cat.ToString(CultureInfo.InvariantCulture));
        }

        using var doc = await GetJsonAsync(sb.ToString(), ct).ConfigureAwait(false);
        return ParseSearchResults(doc.RootElement);
    }

    internal static IReadOnlyList<TorrentResult> ParseSearchResults(JsonElement root)
    {
        var results = new List<TorrentResult>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in root.EnumerateArray())
        {
            var protocol = item.GetStringOrNull("protocol");
            if (protocol is not null && !protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var magnet = item.GetStringOrNull("magnetUrl");
            var download = item.GetStringOrNull("downloadUrl");
            if (magnet is null && download is null)
            {
                continue;
            }

            results.Add(new TorrentResult
            {
                Guid = item.GetStringOrNull("guid") ?? download ?? magnet,
                Title = item.GetStringOrNull("title") ?? string.Empty,
                SizeBytes = item.GetLong("size"),
                Seeders = item.GetInt("seeders"),
                Leechers = item.GetInt("leechers"),
                PublishDate = item.TryGetProperty("publishDate", out var d) && d.ValueKind == JsonValueKind.String && d.TryGetDateTime(out var dt) ? dt : null,
                InfoHash = TorrentHash.Normalize(item.GetStringOrNull("infoHash")) ?? TorrentHash.FromMagnet(magnet),
                MagnetUrl = magnet,
                DownloadUrl = download,
            });
        }

        return results;
    }

    /// <summary>
    /// Télécharge le .torrent via Prowlarr. Certains indexeurs redirigent vers un lien magnet :
    /// les redirections sont donc suivies manuellement.
    /// </summary>
    public async Task<TorrentSource> DownloadAsync(string downloadUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = new Uri(downloadUrl);
        for (int i = 0; i <= MaxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _config.Current.ProwlarrApiKey);
            using var response = await SendAsync(client, request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new HttpRequestException("Redirection Prowlarr sans destination.");
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
                throw new HttpRequestException($"Téléchargement du .torrent refusé par Prowlarr (HTTP {(int)response.StatusCode}).");
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

    private static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and < 400;

    /// <summary>Envoie la requête en remplaçant les erreurs réseau par un message lisible.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, option, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("Prowlarr injoignable : " + NetworkErrors.Describe(ex, _certificates?.Get(request.RequestUri?.Host)), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Prowlarr injoignable : " + NetworkErrors.Describe(ex, _certificates?.Get(request.RequestUri?.Host)), ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (string.IsNullOrWhiteSpace(cfg.ProwlarrUrl) || string.IsNullOrWhiteSpace(cfg.ProwlarrApiKey))
        {
            throw new InvalidOperationException("Prowlarr n'est pas configuré (URL et clé API).");
        }

        if (!Uri.TryCreate(cfg.ProwlarrUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL de Prowlarr invalide (attendu : http://adresse:9696).");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relativeUrl));
        request.Headers.Add("X-Api-Key", cfg.ProwlarrApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await SendAsync(client, request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("Prowlarr a refusé la clé API (HTTP 401).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Prowlarr a répondu HTTP {(int)response.StatusCode}.");
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int GetInt(this JsonElement e, string name)
        => (int)Math.Clamp(e.GetLong(name), int.MinValue, int.MaxValue);

    public static long GetLong(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return 0;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)v.GetDouble(),
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => 0,
        };
    }

    public static double? GetDoubleOrNull(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static bool GetBool(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<int> ParseIntList(string? csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1)
            .Where(i => i > 0);
}
