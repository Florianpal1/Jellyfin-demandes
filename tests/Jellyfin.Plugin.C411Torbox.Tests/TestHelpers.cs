using System.Net;
using System.Text;
using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

internal sealed class FakeConfig : IConfigProvider
{
    public PluginConfiguration Current { get; set; } = new()
    {
        ProwlarrUrl = "http://prowlarr:9696",
        ProwlarrApiKey = "prowlarr-key",
        ProwlarrIndexerId = 7,
        IndexerMode = IndexerMode.Prowlarr,
        TorznabUrl = "https://c411.org/api",
        TorznabApiKey = "c411-key",
        TorboxApiKey = "torbox-key",
    };
}

/// <summary>Handler HTTP programmable : enregistre les requêtes et répond via un délégué.</summary>
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public FakeHttp(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Calls)
        {
            Calls.Add((request, body));
        }

        return _responder(request, body);
    }
}

internal sealed class FakeLibrary : ILibraryLookup
{
    public bool MovieInLibrary { get; set; }

    public int Episodes { get; set; }

    public bool IsMovieInLibrary(IReadOnlyDictionary<string, string> providerIds) => MovieInLibrary;

    public bool IsSeriesInLibrary(IReadOnlyDictionary<string, string> providerIds) => Episodes > 0;

    /// <summary>Épisodes précis présents (saison, épisode) ; s'ils sont renseignés, remplacent <see cref="Episodes"/>.</summary>
    public HashSet<(int Season, int Episode)> PresentEpisodes { get; } = new();

    public string? SeriesFolder { get; set; }

    public List<string> MovieFolders { get; } = new();

    public List<string> SeriesFolders { get; } = new();

    public List<string> BookFolders { get; } = new();

    public bool BookInLibrary { get; set; }

    /// <summary>Livre arrivé (par identifiant, dossier ou titre) ; les derniers arguments reçus sont gardés.</summary>
    public bool BookAvailable { get; set; }

    public (IReadOnlyDictionary<string, string> Ids, string Title, string? Folder)? LastBookCheck { get; private set; }

    public bool IsBookInLibrary(IReadOnlyDictionary<string, string> providerIds) => BookInLibrary;

    public bool IsBookAvailable(IReadOnlyDictionary<string, string> providerIds, string title, string? folder, DateTime since)
    {
        LastBookCheck = (providerIds, title, folder);
        return BookAvailable;
    }

    public int CountEpisodes(IReadOnlyDictionary<string, string> providerIds, int season, int? episode)
        => PresentEpisodes.Count > 0
            ? PresentEpisodes.Count(p => p.Season == season && (episode is null || p.Episode == episode))
            : episode.HasValue ? Math.Min(Episodes, 1) : Episodes;

    public string? GetSeriesFolder(IReadOnlyDictionary<string, string> providerIds) => SeriesFolder;

    public string? GetSeasonFolder(IReadOnlyDictionary<string, string> providerIds, int season) => null;

    public IReadOnlyList<string> GetLibraryFolders(Models.MediaKind kind) => kind switch
    {
        Models.MediaKind.Movie => MovieFolders,
        Models.MediaKind.Book => BookFolders,
        _ => SeriesFolders,
    };
}

internal sealed class FakeNotifications : INotificationService
{
    public List<(Guid? UserId, string Message)> Sent { get; } = new();

    public Task NotifyAdminsAsync(string message, long? requestId, CancellationToken ct)
    {
        Sent.Add((null, message));
        return Task.CompletedTask;
    }

    public Task NotifyUserAsync(Guid userId, string message, long? requestId, CancellationToken ct)
    {
        Sent.Add((userId, message));
        return Task.CompletedTask;
    }
}

internal static class Bencode
{
    /// <summary>Construit un .torrent minimal ; renvoie aussi les octets bruts du dictionnaire « info ».</summary>
    public static (byte[] Torrent, byte[] Info) MakeTorrent(string name)
    {
        var info = $"d6:lengthi1048576e4:name{name.Length}:{name}12:piece lengthi262144e6:pieces20:{new string('a', 20)}e";
        const string Announce = "http://tracker.example/announce";
        var torrent = $"d8:announce{Announce.Length}:{Announce}13:creation datei1700000000e4:info{info}e";
        return (Encoding.ASCII.GetBytes(torrent), Encoding.ASCII.GetBytes(info));
    }
}
