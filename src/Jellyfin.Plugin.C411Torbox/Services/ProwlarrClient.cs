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
public class ProwlarrClient : ITorrentIndexer
{
    public const string HttpClientName = TorrentFileDownloader.HttpClientName;

    private const string SourceName = "Prowlarr";

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

    public string DisplayName => SourceName;

    /// <summary>
    /// Requêtes essayées dans l'ordre : par identifiant d'abord (plus précis), puis par titre
    /// si l'indexeur ne supporte pas la recherche par identifiant.
    /// </summary>
    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(TorrentSearchRequest request, CancellationToken ct)
    {
        HttpRequestException? lastError = null;
        foreach (var query in BuildQueries(request, _config.Current))
        {
            try
            {
                var results = await SearchAsync(query, ct).ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (HttpRequestException ex)
            {
                // Une recherche par identifiant peut être refusée par l'indexeur : on passe à la suivante.
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return Array.Empty<TorrentResult>();
    }

    public static IReadOnlyList<ProwlarrQuery> BuildQueries(TorrentSearchRequest request, Configuration.PluginConfiguration cfg)
    {
        var ids = LibraryLookup.FilterIds(request.ProviderIds);
        var queries = new List<ProwlarrQuery>();
        if (request.Kind == MediaKind.Movie)
        {
            var cats = JsonElementExtensions.ParseIntList(cfg.MovieCategories).ToList();
            if (ids.TryGetValue("Imdb", out var imdb))
            {
                queries.Add(new ProwlarrQuery($"{{ImdbId:{imdb}}}", "movie", cats));
            }
            else if (ids.TryGetValue("Tmdb", out var tmdb))
            {
                queries.Add(new ProwlarrQuery($"{{TmdbId:{tmdb}}}", "movie", cats));
            }

            var text = request.Year.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{request.Name} {request.Year}")
                : request.Name;
            queries.Add(new ProwlarrQuery(text, "search", cats));
        }
        else
        {
            var cats = JsonElementExtensions.ParseIntList(cfg.TvCategories).ToList();
            var season = request.Season!.Value;
            var epToken = request.Episode.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{{Episode:{request.Episode.Value}}}")
                : string.Empty;
            var seasonToken = string.Create(CultureInfo.InvariantCulture, $"{{Season:{season}}}");
            if (ids.TryGetValue("Tvdb", out var tvdb))
            {
                queries.Add(new ProwlarrQuery($"{{TvdbId:{tvdb}}}{seasonToken}{epToken}", "tvsearch", cats));
            }
            else if (ids.TryGetValue("Imdb", out var imdb))
            {
                queries.Add(new ProwlarrQuery($"{{ImdbId:{imdb}}}{seasonToken}{epToken}", "tvsearch", cats));
            }

            var sxx = request.Episode.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{request.Episode.Value:00}")
                : string.Create(CultureInfo.InvariantCulture, $"S{season:00}");
            queries.Add(new ProwlarrQuery($"{request.Name} {sxx}", "search", cats));
        }

        return queries;
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var indexers = await GetIndexersAsync(ct).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"Connexion à Prowlarr réussie : {indexers.Count} indexeur(s).");
    }

    public Task<TorrentSource> DownloadAsync(string downloadUrl, CancellationToken ct)
        => TorrentFileDownloader.DownloadAsync(
            _httpClientFactory.CreateClient(HttpClientName),
            downloadUrl,
            SourceName,
            _certificates,
            r => r.Headers.Add("X-Api-Key", _config.Current.ProwlarrApiKey),
            ct);

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
        using var response = await TorrentFileDownloader.SendAsync(client, request, HttpCompletionOption.ResponseContentRead, SourceName, _certificates, ct).ConfigureAwait(false);
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
