using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Une requête Torznab : paramètres de l'URL (hors clé API).</summary>
public sealed record TorznabQuery(IReadOnlyList<KeyValuePair<string, string>> Parameters)
{
    public string Describe() => string.Join("&", Parameters.Select(p => p.Key + "=" + p.Value));
}

/// <summary>
/// Client Torznab (API « newznab » de C411, la même que celle configurée dans Radarr/Sonarr :
/// https://c411.org/api + clé API). Aucune dépendance à Prowlarr.
/// </summary>
public class TorznabClient : ITorrentIndexer
{
    private const string SourceName = "C411";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _config;
    private readonly CertificateDiagnostics? _certificates;

    public TorznabClient(IHttpClientFactory httpClientFactory, IConfigProvider config, CertificateDiagnostics? certificates = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _certificates = certificates;
    }

    public string DisplayName => SourceName;

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(TorrentSearchRequest request, Func<TorrentResult, bool> accept, CancellationToken ct)
    {
        foreach (var query in BuildQueries(request, _config.Current))
        {
            var results = (await QueryAsync(query, ct).ConfigureAwait(false)).Where(accept).ToList();
            if (results.Count > 0)
            {
                return results;
            }
        }

        return Array.Empty<TorrentResult>();
    }

    public Task<TorrentSource> DownloadAsync(string downloadUrl, CancellationToken ct)
        => TorrentFileDownloader.DownloadAsync(
            _httpClientFactory.CreateClient(TorrentFileDownloader.HttpClientName), downloadUrl, SourceName, _certificates, null, ct);

    public async Task<string> TestAsync(CancellationToken ct)
    {
        // Une vraie recherche (limit=1) : vérifie à la fois l'adresse, le certificat et la clé API.
        await QueryAsync(new TorznabQuery([new("t", "search"), new("q", "test"), new("limit", "1")]), ct).ConfigureAwait(false);
        return "Connexion à C411 réussie (clé API acceptée).";
    }

    /// <summary>
    /// Requêtes à essayer dans l'ordre : par identifiant TMDB (le plus fiable), puis IMDb, puis par titre.
    /// C411 annonce (t=caps) : movie-search q,imdbid,tmdbid ; tv-search q,season,ep,tmdbid,imdbid.
    /// Livres : recherche par titre uniquement.
    /// </summary>
    public static IReadOnlyList<TorznabQuery> BuildQueries(TorrentSearchRequest request, Configuration.PluginConfiguration cfg)
    {
        var ids = LibraryLookup.FilterIds(request.ProviderIds);
        var queries = new List<TorznabQuery>();
        var isMovie = request.Kind == MediaKind.Movie;
        var cats = string.Join(",", JsonElementExtensions.ParseIntList(request.Kind switch
        {
            MediaKind.Movie => cfg.MovieCategories,
            MediaKind.Book => cfg.BookCategories,
            _ => cfg.TvCategories,
        }));

        List<KeyValuePair<string, string>> Base(string type)
        {
            var p = new List<KeyValuePair<string, string>> { new("t", type), new("limit", "100") };
            if (cats.Length > 0)
            {
                p.Add(new("cat", cats));
            }

            return p;
        }

        void AddEpisodeParams(List<KeyValuePair<string, string>> p)
        {
            if (!isMovie)
            {
                p.Add(new("season", request.Season!.Value.ToString(CultureInfo.InvariantCulture)));
                if (request.Episode.HasValue)
                {
                    p.Add(new("ep", request.Episode.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        // Livres : pas d'identifiant connu de C411, recherche par titre seul (l'année figure rarement dans le nom).
        if (request.Kind == MediaKind.Book)
        {
            var bookQuery = Base("search");
            bookQuery.Add(new("q", request.Name));
            queries.Add(new TorznabQuery(bookQuery));
            return queries;
        }

        var idType = isMovie ? "movie" : "tvsearch";
        if (ids.TryGetValue("Tmdb", out var tmdb))
        {
            var p = Base(idType);
            p.Add(new("tmdbid", tmdb));
            AddEpisodeParams(p);
            queries.Add(new TorznabQuery(p));
        }

        if (ids.TryGetValue("Imdb", out var imdb))
        {
            var p = Base(idType);

            // Convention newznab (Radarr/Sonarr) : identifiant IMDb sans le préfixe « tt ».
            p.Add(new("imdbid", imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdb[2..] : imdb));
            AddEpisodeParams(p);
            queries.Add(new TorznabQuery(p));
        }

        foreach (var text in TextQueries(request))
        {
            var textQuery = Base("search");
            textQuery.Add(new("q", text));
            queries.Add(new TorznabQuery(textQuery));
        }

        return queries;
    }

    /// <summary>
    /// Recherches par titre, titre principal puis titres alternatifs : « Dune 2021 », « The Bear S02E03 », « The Bear S02 ».
    /// </summary>
    internal static IEnumerable<string> TextQueries(TorrentSearchRequest request)
    {
        var suffix = request.Kind == MediaKind.Movie
            ? (request.Year.HasValue ? string.Create(CultureInfo.InvariantCulture, $" {request.Year}") : string.Empty)
            : request.Episode.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $" S{request.Season:00}E{request.Episode:00}")
                : string.Create(CultureInfo.InvariantCulture, $" S{request.Season:00}");
        return new[] { request.Name }
            .Concat(request.AlternateNames)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => n + suffix);
    }

    internal async Task<IReadOnlyList<TorrentResult>> QueryAsync(TorznabQuery query, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (string.IsNullOrWhiteSpace(cfg.TorznabUrl) || string.IsNullOrWhiteSpace(cfg.TorznabApiKey))
        {
            throw new InvalidOperationException("C411 n'est pas configuré (URL et clé API).");
        }

        if (!Uri.TryCreate(cfg.TorznabUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL Torznab invalide (attendu : https://c411.org/api).");
        }

        var sb = new StringBuilder(BuildEndpoint(baseUri));
        sb.Append(sb.ToString().Contains('?', StringComparison.Ordinal) ? '&' : '?');
        sb.Append("apikey=").Append(Uri.EscapeDataString(cfg.TorznabApiKey.Trim()));
        foreach (var (key, value) in query.Parameters)
        {
            sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        var client = _httpClientFactory.CreateClient(TorrentFileDownloader.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
        request.Headers.Accept.ParseAdd("application/rss+xml, application/xml, text/xml");
        using var response = await TorrentFileDownloader.SendAsync(client, request, HttpCompletionOption.ResponseContentRead, SourceName, _certificates, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            throw new HttpRequestException(string.Create(
                CultureInfo.InvariantCulture,
                $"C411 a répondu HTTP {(int)response.StatusCode} avec une réponse illisible (l'URL est-elle bien celle de l'API, ex. https://c411.org/api ?)."));
        }

        ThrowIfError(doc, response.StatusCode);
        return ParseResults(doc);
    }

    /// <summary>
    /// Accepte « https://c411.org », « https://c411.org/api » ou une URL avec chemin personnalisé.
    /// Sans chemin, « /api » est ajouté (comme le font Radarr/Sonarr avec « API Path = /api »).
    /// </summary>
    internal static string BuildEndpoint(Uri baseUri)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/api";
        }

        return baseUri.GetLeftPart(UriPartial.Authority) + path + baseUri.Query;
    }

    internal static void ThrowIfError(XDocument doc, HttpStatusCode status)
    {
        var error = doc.Root?.Name.LocalName == "error" ? doc.Root : null;
        if (error is not null)
        {
            var code = (string?)error.Attribute("code");
            var description = (string?)error.Attribute("description") ?? "erreur inconnue";
            throw new HttpRequestException(code is "100" or "101" or "102"
                ? "C411 a refusé la clé API (" + description + ")."
                : "C411 a renvoyé une erreur : " + description + ".");
        }

        if ((int)status >= 400)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"C411 a répondu HTTP {(int)status}."));
        }
    }

    internal static IReadOnlyList<TorrentResult> ParseResults(XDocument doc)
    {
        var results = new List<TorrentResult>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var attrs = item.Elements()
                .Where(e => e.Name.LocalName == "attr")
                .Select(e => (Name: ((string?)e.Attribute("name"))?.ToLowerInvariant(), Value: (string?)e.Attribute("value")))
                .Where(a => a.Name is not null && a.Value is not null)
                .GroupBy(a => a.Name!)
                .ToDictionary(g => g.Key, g => g.First().Value!);

            string? Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            long Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

            var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure");
            var magnet = attrs.GetValueOrDefault("magneturl");
            var download = (string?)enclosure?.Attribute("url") ?? Child("link");
            if (string.IsNullOrWhiteSpace(download) && string.IsNullOrWhiteSpace(magnet))
            {
                continue;
            }

            var seeders = (int)Math.Clamp(Long(attrs.GetValueOrDefault("seeders")), 0, int.MaxValue);
            var leechers = attrs.TryGetValue("leechers", out var l)
                ? (int)Math.Clamp(Long(l), 0, int.MaxValue)
                : (int)Math.Clamp(Long(attrs.GetValueOrDefault("peers")) - seeders, 0, int.MaxValue);
            var size = Long(Child("size"));
            if (size <= 0)
            {
                size = Long(attrs.GetValueOrDefault("size"));
            }

            if (size <= 0)
            {
                size = Long((string?)enclosure?.Attribute("length"));
            }

            results.Add(new TorrentResult
            {
                Guid = Child("guid") ?? download ?? magnet,
                Title = Child("title") ?? string.Empty,
                SizeBytes = size,
                Seeders = seeders,
                Leechers = leechers,
                PublishDate = DateTimeOffset.TryParse(Child("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date.UtcDateTime
                    : null,
                InfoHash = TorrentHash.Normalize(attrs.GetValueOrDefault("infohash")) ?? TorrentHash.FromMagnet(magnet),
                MagnetUrl = string.IsNullOrWhiteSpace(magnet) ? null : magnet,
                DownloadUrl = string.IsNullOrWhiteSpace(download) ? null : download,
                TmdbId = Id(attrs.GetValueOrDefault("tmdbid") ?? attrs.GetValueOrDefault("tmdb")),
                ImdbId = Id(attrs.GetValueOrDefault("imdbid") ?? attrs.GetValueOrDefault("imdb")),
                TvdbId = Id(attrs.GetValueOrDefault("tvdbid") ?? attrs.GetValueOrDefault("tvdb")),
            });
        }

        return results;
    }

    /// <summary>Identifiant renseigné, ou null (vide et « 0 » = inconnu).</summary>
    internal static string? Id(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Trim().TrimStart('0').Length == 0 ? null : value.Trim();
}
