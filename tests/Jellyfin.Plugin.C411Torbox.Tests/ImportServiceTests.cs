using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Jellyfin.Plugin.C411Torbox.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

internal sealed class FakeTitles : IEpisodeTitleProvider
{
    public Dictionary<(int Season, int Episode), string> Titles { get; } = new();

    public Task<IReadOnlyDictionary<(int Season, int Episode), string>> GetTitlesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, IEnumerable<(int Season, int Episode)> episodes, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<(int, int), string>>(Titles);
}

internal sealed class FakeRefresher : ILibraryRefresher
{
    public List<string> Paths { get; } = new();

    public void Refresh(string path) => Paths.Add(path);
}

/// <summary>Import complet : Torbox et son CDN simulés, vraie base SQLite, vrais fichiers dans un dossier temporaire.</summary>
public sealed class ImportServiceTests : IDisposable
{
    private const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "c411torbox-import-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfig _config = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeNotifications _notifications = new();
    private readonly FakeTitles _titles = new();
    private readonly FakeRefresher _refresher = new();
    private readonly RequestRepository _repository;
    private readonly RequestService _requests;
    private readonly ImportService _import;
    private readonly FakeHttp _http;

    private string _files = "[]";

    public ImportServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Films"));
        Directory.CreateDirectory(Path.Combine(_dir, "Series"));
        _config.Current.AutoImportEnabled = true;
        _config.Current.MoviesPath = Path.Combine(_dir, "Films");
        _config.Current.SeriesPath = Path.Combine(_dir, "Series");
        _repository = new RequestRepository(Path.Combine(_dir, "requests.db"));
        _http = new FakeHttp(Respond);
        _requests = new RequestService(
            _repository,
            new SearchResultCache(),
            new IndexerProvider(new ProwlarrClient(_http, _config), new TorznabClient(_http, _config), _config),
            new TorboxClient(_http, _config),
            _library,
            _notifications,
            _config,
            NullLogger<RequestService>.Instance);
        _import = new ImportService(
            _repository,
            _requests,
            new TorboxClient(_http, _config),
            new FileDownloader(_http),
            _library,
            _titles,
            _refresher,
            _notifications,
            _config,
            NullLogger<ImportService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Content(long fileId, int size)
        => Enumerable.Range(0, size).Select(i => (byte)((i + fileId) % 251)).ToArray();

    private IEnumerable<HttpRequestMessage> Calls(string pathEnd)
        => _http.Calls.Select(c => c.Request).Where(r => r.RequestUri!.AbsolutePath.EndsWith(pathEnd, StringComparison.Ordinal));

    private HttpResponseMessage Respond(HttpRequestMessage req, string body)
    {
        var uri = req.RequestUri!;
        if (uri.Host == "cdn.torbox.test")
        {
            var fileId = long.Parse(uri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            var size = int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["size"]!, System.Globalization.CultureInfo.InvariantCulture);
            var bytes = Content(fileId, size);
            if (req.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
            {
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)from..]) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, size - 1, size);
                return partial;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (uri.AbsolutePath.EndsWith("/mylist", StringComparison.Ordinal))
        {
            return FakeHttp.Json($$"""
                { "success": true, "data": { "id": 99, "hash": "{{Hash}}", "name": "torrent", "progress": 1, "download_finished": true,
                  "files": {{_files}} } }
                """);
        }

        if (uri.AbsolutePath.EndsWith("/requestdl", StringComparison.Ordinal))
        {
            Assert.Equal("torbox-key", query["token"]);
            var fileId = query["file_id"];
            var size = System.Text.Json.JsonDocument.Parse(_files).RootElement.EnumerateArray()
                .First(f => f.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) == fileId)
                .GetProperty("size").GetInt64();
            return FakeHttp.Json($$"""{ "success": true, "data": "https://cdn.torbox.test/dl/{{fileId}}?size={{size}}" }""");
        }

        if (uri.AbsolutePath.EndsWith("/controltorrent", StringComparison.Ordinal))
        {
            Assert.Contains("\"delete\"", body, StringComparison.Ordinal);
            return FakeHttp.Json("""{ "success": true }""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private MediaRequest AddCompleted(MediaKind kind, RequestScope scope, string title, int? year, int? season = null, int? episode = null)
        => _repository.Insert(new MediaRequest
        {
            UserId = Guid.NewGuid(),
            UserName = "alice",
            MediaKind = kind,
            Scope = scope,
            MediaTitle = title,
            Year = year,
            TmdbId = kind == MediaKind.Movie ? "438631" : "136315",
            Season = season,
            Episode = episode,
            TorrentTitle = kind == MediaKind.Movie ? "Dune.2021.MULTi.1080p.BluRay.x264" : "The.Bear.S01.MULTi.1080p.WEB-DL",
            InfoHash = Hash,
            TorboxTorrentId = 99,
            Status = RequestStatus.CompletedOnTorbox,
        });

    [Fact]
    public async Task Movie_IsDownloaded_AndFiledLikeRadarr()
    {
        _files = """
            [ { "id": 1, "name": "Dune.2021/Dune.2021.MULTi.1080p.BluRay.x264.mkv", "size": 5000 },
              { "id": 2, "name": "Dune.2021/Dune.FRENCH.srt", "size": 30 },
              { "id": 3, "name": "Dune.2021/Sample/sample.mkv", "size": 10 } ]
            """;
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        Assert.True(await _import.ProcessNextAsync(CancellationToken.None));

        var folder = Path.Combine(_dir, "Films", "Dune (2021) [tmdbid-438631]");
        var video = Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv");
        Assert.Equal(Content(1, 5000), File.ReadAllBytes(video));
        Assert.Equal(Content(2, 30), File.ReadAllBytes(Path.Combine(folder, "Dune (2021) Bluray-1080p.fr.srt")));
        Assert.Equal(2, Directory.GetFiles(folder).Length);

        var saved = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.Imported, saved.Status);
        Assert.Equal(1, saved.ImportProgress);
        Assert.Equal(folder, saved.ImportedPath);
        Assert.Contains(folder, _refresher.Paths);
        Assert.Empty(Calls("/controltorrent"));

        // Rien d'autre à faire ensuite.
        Assert.False(await _import.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteFromTorbox_WhenEnabled()
    {
        _config.Current.DeleteFromTorboxAfterImport = true;
        _files = """[ { "id": 1, "name": "Dune.mkv", "size": 100 } ]""";
        AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await _import.ProcessNextAsync(CancellationToken.None);

        Assert.Single(Calls("/controltorrent"));
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _config.Current.AutoImportEnabled = false;
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        Assert.False(await _import.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);
        Assert.Empty(_http.Calls);
    }

    [Fact]
    public async Task AlreadyInLibrary_IsMarkedAvailable_WithoutDownloading()
    {
        _library.MovieInLibrary = true;
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await _import.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.Available, _repository.Get(r.Id)!.Status);
        Assert.Empty(Calls("/requestdl"));
    }

    [Fact]
    public async Task MissingFolder_Fails_ThenRetryRequeues()
    {
        _config.Current.MoviesPath = Path.Combine(_dir, "nexistepas");
        _files = """[ { "id": 1, "name": "Dune.mkv", "size": 100 } ]""";
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await _import.ProcessNextAsync(CancellationToken.None);

        var failed = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.ImportFailed, failed.Status);
        Assert.Contains("introuvable", failed.LastError, StringComparison.Ordinal);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.Contains("Échec du rangement", StringComparison.Ordinal));

        _config.Current.MoviesPath = Path.Combine(_dir, "Films");
        var retried = await _requests.RetryImportAsync(r.Id, CancellationToken.None);
        Assert.Equal(RequestStatus.CompletedOnTorbox, retried.Status);

        await _import.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Imported, _repository.Get(r.Id)!.Status);
    }

    [Fact]
    public async Task InterruptedDownload_IsResumed()
    {
        _files = """[ { "id": 1, "name": "Dune.2021.1080p.BluRay.mkv", "size": 4000 } ]""";
        var folder = Path.Combine(_dir, "Films", "Dune (2021) [tmdbid-438631]");
        Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv");
        File.WriteAllBytes(final + FileDownloader.PartialExtension, Content(1, 4000)[..1500]);
        AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await _import.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(Content(1, 4000), File.ReadAllBytes(final));
        Assert.False(File.Exists(final + FileDownloader.PartialExtension));
        var cdn = _http.Calls.Select(c => c.Request).Single(c => c.RequestUri!.Host == "cdn.torbox.test");
        Assert.Equal(1500, cdn.Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task SeasonPack_GoesIntoExistingSeriesFolder_WithEpisodeTitles()
    {
        var existing = Path.Combine(_dir, "Series", "The Bear");
        Directory.CreateDirectory(existing);
        _library.SeriesFolder = existing;
        _titles.Titles[(1, 1)] = "Système";
        _files = """
            [ { "id": 1, "name": "The.Bear.S01/The.Bear.S01E01.1080p.WEB-DL.mkv", "size": 200 },
              { "id": 2, "name": "The.Bear.S01/The.Bear.S01E02.1080p.WEB-DL.mkv", "size": 300 } ]
            """;
        var r = AddCompleted(MediaKind.Series, RequestScope.Season, "The Bear", 2022, season: 1);

        await _import.ProcessNextAsync(CancellationToken.None);

        var season = Path.Combine(existing, "Season 01");
        Assert.True(File.Exists(Path.Combine(season, "The Bear - S01E01 - Système WEBDL-1080p.mkv")));
        Assert.True(File.Exists(Path.Combine(season, "The Bear - S01E02 WEBDL-1080p.mkv")));
        Assert.Equal(RequestStatus.Imported, _repository.Get(r.Id)!.Status);
    }

    [Fact]
    public async Task SiblingRequests_OfTheSameTorrent_AreImportedTogether()
    {
        _files = """[ { "id": 1, "name": "Dune.mkv", "size": 100 } ]""";
        var a = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);
        var b = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await _import.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.Imported, _repository.Get(a.Id)!.Status);
        Assert.Equal(RequestStatus.Imported, _repository.Get(b.Id)!.Status);
        Assert.Single(_http.Calls, c => c.Request.RequestUri!.Host == "cdn.torbox.test");
    }

    [Fact]
    public async Task InterruptedImport_IsResetOnStartup()
    {
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);
        r.Status = RequestStatus.Importing;
        _repository.Update(r);

        await _requests.ResetInterruptedImportsAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);
    }

    [Fact]
    public void Repository_MigratesV1Database()
    {
        var path = Path.Combine(_dir, "v1.db");
        _ = new RequestRepository(path);
        using (var conn = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Requests DROP COLUMN ImportProgress; ALTER TABLE Requests DROP COLUMN ImportedPath; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        var repo = new RequestRepository(path);
        var r = repo.Insert(new MediaRequest { UserId = Guid.NewGuid(), MediaTitle = "Dune", Status = RequestStatus.Imported });
        r.ImportedPath = "/films/Dune";
        r.ImportProgress = 1;
        repo.Update(r);

        var saved = repo.Get(r.Id)!;
        Assert.Equal("/films/Dune", saved.ImportedPath);
        Assert.Equal(1, saved.ImportProgress);
    }
}
