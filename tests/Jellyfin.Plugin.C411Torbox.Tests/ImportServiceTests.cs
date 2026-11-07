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

    public Task<IReadOnlyList<(int Episode, string? Title)>> GetSeasonEpisodesAsync(
        IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(int, string?)>>(Titles
            .Where(t => t.Key.Season == season)
            .OrderBy(t => t.Key.Episode)
            .Select(t => (t.Key.Episode, (string?)t.Value))
            .ToList());
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
            new TorboxSubmitter(
                new IndexerProvider(new ProwlarrClient(_http, _config), new TorznabClient(_http, _config), _config),
                new TorboxClient(_http, _config),
                NullLogger<TorboxSubmitter>.Instance),
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

    /// <summary>Enchaîne les tentatives de rangement automatiques (sans attendre leur délai) jusqu'à l'échec définitif.</summary>
    private async Task ImportUntilFinalFailureAsync(long id)
    {
        for (int i = 0; i < ImportService.MaxImportAttempts; i++)
        {
            var r = _repository.Get(id)!;
            if (r.Status != RequestStatus.CompletedOnTorbox)
            {
                break;
            }

            r.RetryImportAfter = null;
            _repository.Update(r);
            await _import.ProcessNextAsync(CancellationToken.None);
        }
    }

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
    public async Task ExistingDifferentFile_IsNeverOverwritten()
    {
        _files = """[ { "id": 1, "name": "Dune.2021.MULTi.1080p.BluRay.x264.mkv", "size": 5000 } ]""";
        var folder = Path.Combine(_dir, "Films", "Dune (2021) [tmdbid-438631]");
        var video = Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(video, new byte[] { 1, 2, 3 });
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);

        await ImportUntilFinalFailureAsync(r.Id);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(video));
        var saved = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.ImportFailed, saved.Status);
        Assert.Contains("n'est pas écrasé", saved.LastError, StringComparison.Ordinal);
        Assert.Empty(Calls("/requestdl"));
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

        // Premier échec : nouvel essai automatique plus tard, sans déranger les admins.
        await _import.ProcessNextAsync(CancellationToken.None);
        var waiting = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.CompletedOnTorbox, waiting.Status);
        Assert.Equal(1, waiting.ImportAttempts);
        Assert.NotNull(waiting.RetryImportAfter);
        Assert.Contains("introuvable", waiting.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(_notifications.Sent, n => n.Message.Contains("Échec du rangement", StringComparison.Ordinal));

        // Le délai n'est pas écoulé : rien à faire.
        Assert.False(await _import.ProcessNextAsync(CancellationToken.None));

        await ImportUntilFinalFailureAsync(r.Id);
        var failed = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.ImportFailed, failed.Status);
        Assert.Equal(ImportService.MaxImportAttempts, failed.ImportAttempts);
        Assert.Contains("introuvable", failed.LastError, StringComparison.Ordinal);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.Contains("Échec du rangement", StringComparison.Ordinal));

        _config.Current.MoviesPath = Path.Combine(_dir, "Films");
        var retried = await _requests.RetryImportAsync(r.Id, CancellationToken.None);
        Assert.Equal(RequestStatus.CompletedOnTorbox, retried.Status);
        Assert.Equal(0, retried.ImportAttempts);

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
    public async Task TransientFailure_IsRetriedAutomatically_ThenSucceeds()
    {
        _config.Current.MoviesPath = Path.Combine(_dir, "nexistepas");
        _files = """[ { "id": 1, "name": "Dune.mkv", "size": 100 } ]""";
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);
        await _import.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(1, _repository.Get(r.Id)!.ImportAttempts);

        // Le partage NAS est revenu avant le nouvel essai.
        _config.Current.MoviesPath = Path.Combine(_dir, "Films");
        await ImportUntilFinalFailureAsync(r.Id);

        var saved = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.Imported, saved.Status);
        Assert.Equal(0, saved.ImportAttempts);
        Assert.Null(saved.LastError);
    }

    [Fact]
    public void RetryDelay_GrowsWithAttempts()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ImportService.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(30), ImportService.RetryDelay(2));
    }

    [Fact]
    public async Task InterruptedWork_IsRecoveredOnStartup()
    {
        var r = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);
        r.Status = RequestStatus.Importing;
        _repository.Update(r);
        var sending = AddCompleted(MediaKind.Movie, RequestScope.Movie, "Dune", 2021);
        sending.Status = RequestStatus.Approved;
        _repository.Update(sending);

        await _requests.RecoverInterruptedAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);
        var failed = _repository.Get(sending.Id)!;
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Contains("interrompu", failed.LastError, StringComparison.Ordinal);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.Contains("interrompu", StringComparison.Ordinal));
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
            cmd.CommandText = "ALTER TABLE Requests DROP COLUMN MissingOnTorboxSince; ALTER TABLE Requests DROP COLUMN ImportAttempts; ALTER TABLE Requests DROP COLUMN RetryImportAfter; ALTER TABLE Requests DROP COLUMN StaleNotifiedAt; ALTER TABLE Requests DROP COLUMN ImportProgress; ALTER TABLE Requests DROP COLUMN ImportedPath; ALTER TABLE Requests DROP COLUMN BookIds; PRAGMA user_version = 1;";
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

    [Fact]
    public void Repository_MigratesV2Database()
    {
        var path = Path.Combine(_dir, "v2.db");
        _ = new RequestRepository(path);
        using (var conn = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Requests DROP COLUMN MissingOnTorboxSince; ALTER TABLE Requests DROP COLUMN ImportAttempts; ALTER TABLE Requests DROP COLUMN RetryImportAfter; ALTER TABLE Requests DROP COLUMN StaleNotifiedAt; ALTER TABLE Requests DROP COLUMN BookIds; PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        var repo = new RequestRepository(path);
        var r = repo.Insert(new MediaRequest { UserId = Guid.NewGuid(), MediaKind = MediaKind.Book, Scope = RequestScope.Book, MediaTitle = "Astérix", BookIds = "Isbn=978", Status = RequestStatus.Pending });

        Assert.Equal("Isbn=978", repo.Get(r.Id)!.BookIds);
    }

    [Fact]
    public void Repository_MigratesV3Database()
    {
        var path = Path.Combine(_dir, "v3.db");
        _ = new RequestRepository(path);
        using (var conn = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Requests DROP COLUMN MissingOnTorboxSince; ALTER TABLE Requests DROP COLUMN ImportAttempts; ALTER TABLE Requests DROP COLUMN RetryImportAfter; ALTER TABLE Requests DROP COLUMN StaleNotifiedAt; PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }

        var repo = new RequestRepository(path);
        var r = repo.Insert(new MediaRequest { UserId = Guid.NewGuid(), MediaTitle = "Dune", Status = RequestStatus.CompletedOnTorbox });
        var retryAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        r.ImportAttempts = 2;
        r.RetryImportAfter = retryAt;
        r.MissingOnTorboxSince = retryAt;
        r.StaleNotifiedAt = retryAt;
        repo.Update(r);

        var saved = repo.Get(r.Id)!;
        Assert.Equal(2, saved.ImportAttempts);
        Assert.Equal(retryAt, saved.RetryImportAfter);
        Assert.Equal(retryAt, saved.MissingOnTorboxSince);
        Assert.Equal(retryAt, saved.StaleNotifiedAt);
    }
}
