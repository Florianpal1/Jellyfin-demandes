using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.C411Torbox.Services;

public sealed record TorboxCreateResult(long? TorrentId, string? Hash);

public sealed record TorboxTorrent(long Id, string? Hash, string? Name, double Progress, string? DownloadState, bool DownloadFinished)
{
    /// <summary>Fichiers du torrent (renseignés une fois le téléchargement Torbox terminé).</summary>
    public IReadOnlyList<TorboxFile> Files { get; init; } = Array.Empty<TorboxFile>();
}

/// <summary>Fichier d'un torrent Torbox. <paramref name="Name"/> contient le chemin relatif (dossiers du torrent compris).</summary>
public sealed record TorboxFile(long Id, string Name, long Size);

/// <summary>Erreur renvoyée par l'API Torbox.</summary>
public class TorboxException : Exception
{
    public TorboxException(string message)
        : base(message)
    {
    }

    public TorboxException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Client de l'API Torbox v1 (https://api.torbox.app, spécification : /openapi.json).
/// Écritures : ajout (<see cref="CreateTorrentAsync"/>) et, si l'option est cochée, suppression après rangement.
/// </summary>
public class TorboxClient
{
    public const string HttpClientName = "C411Torbox.Torbox";

    private static readonly Uri BaseUri = new("https://api.torbox.app/v1/api/");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _config;

    public TorboxClient(IHttpClientFactory httpClientFactory, IConfigProvider config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <summary>Retourne l'ensemble des infohash (minuscules) déjà en cache sur Torbox.</summary>
    public async Task<HashSet<string>> CheckCachedAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var list = hashes.Select(TorrentHash.Normalize).OfType<string>().Distinct().ToList();
        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0)
        {
            return cached;
        }

        using var request = CreateRequest(HttpMethod.Post, "torrents/checkcached?format=object&list_files=false");
        request.Content = JsonContent.Create(new { hashes = list });
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
        foreach (var hash in ParseCachedHashes(doc.RootElement))
        {
            cached.Add(hash);
        }

        return cached;
    }

    internal static IEnumerable<string> ParseCachedHashes(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in data.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False)
                {
                    continue;
                }

                var hash = TorrentHash.Normalize(prop.Value.ValueKind == JsonValueKind.Object ? prop.Value.GetStringOrNull("hash") : null)
                    ?? TorrentHash.Normalize(prop.Name);
                if (hash is not null)
                {
                    yield return hash;
                }
            }
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var hash = TorrentHash.Normalize(item.ValueKind == JsonValueKind.Object ? item.GetStringOrNull("hash") : item.ValueKind == JsonValueKind.String ? item.GetString() : null);
                if (hash is not null)
                {
                    yield return hash;
                }
            }
        }
    }

    /// <summary>Ajoute un torrent sur Torbox (fichier .torrent prioritaire, sinon magnet).</summary>
    public async Task<TorboxCreateResult> CreateTorrentAsync(TorrentSource source, string name, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        if (source.File is { Length: > 0 })
        {
            var file = new ByteArrayContent(source.File);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(file, "file", "torrent.torrent");
        }
        else if (!string.IsNullOrEmpty(source.Magnet))
        {
            form.Add(new StringContent(source.Magnet), "magnet");
        }
        else
        {
            throw new TorboxException("Aucun fichier .torrent ni lien magnet à envoyer.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            form.Add(new StringContent(name), "name");
        }

        using var request = CreateRequest(HttpMethod.Post, "torrents/createtorrent");
        request.Content = form;
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
        return ParseCreateResult(doc.RootElement);
    }

    internal static TorboxCreateResult ParseCreateResult(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new TorboxCreateResult(null, null);
        }

        long id = data.GetLong("torrent_id");
        return new TorboxCreateResult(id > 0 ? id : null, TorrentHash.Normalize(data.GetStringOrNull("hash")));
    }

    public async Task<IReadOnlyList<TorboxTorrent>> GetMyListAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "torrents/mylist?bypass_cache=true");
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
        return ParseMyList(doc.RootElement);
    }

    internal static IReadOnlyList<TorboxTorrent> ParseMyList(JsonElement root)
    {
        var list = new List<TorboxTorrent>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in data.EnumerateArray())
        {
            list.Add(ParseTorrent(item));
        }

        return list;
    }

    /// <summary>Un torrent précis, avec la liste de ses fichiers (null s'il n'existe plus sur Torbox).</summary>
    public async Task<TorboxTorrent?> GetTorrentAsync(long torrentId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, Invariant($"torrents/mylist?bypass_cache=true&id={torrentId}"));
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        // Selon les versions de l'API, « id » renvoie l'objet seul ou une liste d'un élément.
        return data.ValueKind switch
        {
            JsonValueKind.Object => ParseTorrent(data),
            JsonValueKind.Array => data.EnumerateArray().Select(ParseTorrent).FirstOrDefault(t => t.Id == torrentId),
            _ => null,
        };
    }

    internal static TorboxTorrent ParseTorrent(JsonElement item)
    {
        var files = new List<TorboxFile>();
        if (item.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var name = f.GetStringOrNull("name") ?? f.GetStringOrNull("short_name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    files.Add(new TorboxFile(f.GetLong("id"), name, f.GetLong("size")));
                }
            }
        }

        return new TorboxTorrent(
            item.GetLong("id"),
            TorrentHash.Normalize(item.GetStringOrNull("hash")),
            item.GetStringOrNull("name"),
            item.GetDoubleOrNull("progress") ?? 0,
            item.GetStringOrNull("download_state"),
            item.GetBool("download_finished"))
        {
            Files = files,
        };
    }

    /// <summary>Lien de téléchargement direct (temporaire) d'un fichier d'un torrent terminé.</summary>
    public async Task<Uri> RequestDownloadUrlAsync(long torrentId, long fileId, CancellationToken ct)
    {
        // L'API attend la clé en paramètre « token » sur cet appel ; elle n'est jamais journalisée par le plugin.
        var token = Uri.EscapeDataString(_config.Current.TorboxApiKey.Trim());
        using var request = CreateRequest(
            HttpMethod.Get,
            Invariant($"torrents/requestdl?token={token}&torrent_id={torrentId}&file_id={fileId}&zip_link=false"));
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
        var url = doc.RootElement.GetStringOrNull("data");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new TorboxException("Torbox n'a pas renvoyé de lien de téléchargement.");
        }

        return uri;
    }

    /// <summary>Supprime un torrent de Torbox (les fichiers déjà rangés sur le NAS ne sont pas touchés).</summary>
    public async Task DeleteTorrentAsync(long torrentId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "torrents/controltorrent");
        request.Content = JsonContent.Create(new { torrent_id = torrentId, operation = "delete" });
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
    }

    private static string Invariant(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>Vérifie que la clé API est valide.</summary>
    public async Task TestAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "user/me");
        using var doc = await SendAsync(request, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl)
    {
        var key = _config.Current.TorboxApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new TorboxException("La clé API Torbox n'est pas configurée.");
        }

        var request = new HttpRequestMessage(method, new Uri(BaseUri, relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TorboxException("Torbox injoignable : " + NetworkErrors.Describe(ex), ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }
            catch (JsonException)
            {
                // Corps non JSON : traité ci-dessous.
            }

            var success = doc is not null
                && doc.RootElement.ValueKind == JsonValueKind.Object
                && (!doc.RootElement.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False);

            if (response.IsSuccessStatusCode && success && doc is not null)
            {
                return doc;
            }

            string detail = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.GetStringOrNull("detail") ?? doc.RootElement.GetStringOrNull("error") ?? string.Empty
                : string.Empty;
            doc?.Dispose();
            throw new TorboxException($"Torbox a refusé la requête (HTTP {(int)response.StatusCode}) {detail}".Trim());
        }
    }
}
