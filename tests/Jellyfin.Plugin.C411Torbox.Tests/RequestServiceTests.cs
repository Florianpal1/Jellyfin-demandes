using System.Net;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

/// <summary>Scénarios complets avec une vraie base SQLite et des API Prowlarr/Torbox simulées.</summary>
public sealed class RequestServiceTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "c411torbox-tests-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FakeConfig _config = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeNotifications _notifications = new();
    private readonly SearchResultCache _cache = new();
    private readonly RequestRepository _repository;
    private readonly FakeHttp _http;
    private readonly RequestService _service;

    private bool _torboxFails;
    private string _myList = """{ "success": true, "data": [] }""";

    public RequestServiceTests()
    {
        _repository = new RequestRepository(_dbPath);
        _http = new FakeHttp((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/createtorrent", StringComparison.Ordinal))
            {
                return _torboxFails
                    ? FakeHttp.Json("""{ "success": false, "detail": "Limite atteinte" }""", HttpStatusCode.Forbidden)
                    : FakeHttp.Json($$"""{ "success": true, "data": { "torrent_id": 99, "hash": "{{Hash}}" } }""");
            }

            if (path.EndsWith("/mylist", StringComparison.Ordinal))
            {
                return FakeHttp.Json(_myList);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        _service = new RequestService(
            _repository,
            _cache,
            new IndexerProvider(new ProwlarrClient(_http, _config), new TorznabClient(_http, _config), _config),
            new TorboxClient(_http, _config),
            _library,
            _notifications,
            _config,
            NullLogger<RequestService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }

    private int CreateTorrentCalls => _http.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/createtorrent", StringComparison.Ordinal));

    private string AddResult(long sizeBytes, int seeders, bool cached = false, RequestScope scope = RequestScope.Movie, string? hash = Hash)
    {
        var context = scope == RequestScope.Movie
            ? new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", Year = 2021, ProviderIds = new() { ["Imdb"] = "tt1160419" } }
            : new TorrentSearchRequest { Kind = MediaKind.Series, Name = "The Bear", Season = 1, Episode = scope == RequestScope.Episode ? 2 : null, ProviderIds = new() { ["Tvdb"] = "1" } };
        var result = new TorrentResult
        {
            Title = "Dune.2021.MULTi.1080p",
            SizeBytes = sizeBytes,
            Seeders = seeders,
            InfoHash = hash,
            IsCachedOnTorbox = cached,
            MagnetUrl = "magnet:?xt=urn:btih:" + (hash ?? "0000000000000000000000000000000000000000"),
        };
        return _cache.Add(new CachedResult(result, context, null));
    }

    [Fact]
    public async Task SmallTorrent_IsAutoApproved_AndSentToTorbox()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(4 * Gb, 50), CancellationToken.None);

        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(AutoApprovalReason.Size, r.AutoApprovalReason);
        Assert.Equal(99, r.TorboxTorrentId);
        Assert.Equal(1, CreateTorrentCalls);
        Assert.Equal(RequestStatus.SentToTorbox, _repository.Get(r.Id)!.Status);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.Contains("envoyée à Torbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CachedTorrent_IsAutoApproved_EvenIfHuge()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(80 * Gb, 0, cached: true), CancellationToken.None);

        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(AutoApprovalReason.Cached, r.AutoApprovalReason);
    }

    [Fact]
    public async Task BigTorrent_WaitsForAdmin_ThenApprovalSendsIt()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);
        Assert.Equal(RequestStatus.Pending, r.Status);
        Assert.Equal(0, CreateTorrentCalls);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.StartsWith("Nouvelle demande", StringComparison.Ordinal));

        var approved = await _service.ApproveAsync(r.Id, "admin", CancellationToken.None);

        Assert.Equal(RequestStatus.SentToTorbox, approved.Status);
        Assert.Equal("admin", approved.ReviewedBy);
        Assert.Equal(1, CreateTorrentCalls);
    }

    [Fact]
    public async Task LowSeeders_WaitsForAdmin_EvenIfSmall()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 1), CancellationToken.None);
        Assert.Equal(RequestStatus.Pending, r.Status);
    }

    [Fact]
    public async Task Reject_NotifiesUserWithReason_AndCannotBeApprovedAfterwards()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);

        await _service.RejectAsync(r.Id, "admin", "Trop gros", CancellationToken.None);

        var stored = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.Rejected, stored.Status);
        Assert.Equal("Trop gros", stored.RejectReason);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.EndsWith(": Trop gros", StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.ApproveAsync(r.Id, "admin", CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task TorboxFailure_MarksFailed_AndAdminCanRetry()
    {
        _torboxFails = true;
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        Assert.Equal(RequestStatus.Failed, r.Status);
        Assert.Contains("Limite atteinte", r.LastError, StringComparison.Ordinal);

        _torboxFails = false;
        var retried = await _service.ApproveAsync(r.Id, "admin", CancellationToken.None);
        Assert.Equal(RequestStatus.SentToTorbox, retried.Status);
        Assert.Null(retried.LastError);
    }

    [Fact]
    public async Task SameTorrent_RequestedTwice_IsDeduplicated()
    {
        var first = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);
        var again = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);
        var bob = await _service.CreateAsync(Bob, "bob", AddResult(40 * Gb, 50), CancellationToken.None);

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, bob.Id);
        Assert.Equal(RequestStatus.Pending, bob.Status);

        // Valider l'une envoie le torrent une seule fois et met à jour les deux demandes.
        await _service.ApproveAsync(first.Id, "admin", CancellationToken.None);
        Assert.Equal(1, CreateTorrentCalls);
        Assert.Equal(RequestStatus.SentToTorbox, _repository.Get(bob.Id)!.Status);
    }

    [Fact]
    public async Task AlreadyInLibrary_IsRefused()
    {
        _library.MovieInLibrary = true;
        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task ExpiredResult_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.CreateAsync(Alice, "alice", "inconnu", CancellationToken.None));
        Assert.Equal(410, ex.StatusCode);
    }

    [Fact]
    public async Task StatusSync_ThenLibraryArrival_MakesRequestAvailable()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);

        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 0.4, "download_finished": false } ] }""";
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        var downloading = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.DownloadingOnTorbox, downloading.Status);
        Assert.Equal(0.4, downloading.TorboxProgress);

        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 1, "download_finished": true } ] }""";
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);

        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);

        _library.MovieInLibrary = true;
        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Available, _repository.Get(r.Id)!.Status);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.Contains("disponible dans Jellyfin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeasonPack_BecomesAvailableOnlyWhenNewEpisodesArrive()
    {
        _library.Episodes = 3;
        var r = await _service.CreateAsync(Alice, "alice", AddResult(10 * Gb, 50, scope: RequestScope.Season), CancellationToken.None);
        Assert.Equal(RequestScope.Season, r.Scope);
        Assert.Equal(3, r.EpisodesInLibraryAtRequest);

        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.SentToTorbox, _repository.Get(r.Id)!.Status);

        _library.Episodes = 10;
        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Available, _repository.Get(r.Id)!.Status);
    }

    [Fact]
    public void Repository_KeepsSecretsAndNotifications()
    {
        var r = _repository.Insert(new MediaRequest
        {
            UserId = Alice,
            UserName = "alice",
            MediaTitle = "Dune",
            TorrentTitle = "t",
            DownloadUrl = "http://prowlarr/download?apikey=secret",
            Status = RequestStatus.Pending,
        });
        Assert.Equal("http://prowlarr/download?apikey=secret", _repository.Get(r.Id)!.DownloadUrl);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(_repository.Get(r.Id)), StringComparison.Ordinal);

        _repository.AddNotification(new Notification { UserId = Alice, Message = "pour alice" });
        _repository.AddNotification(new Notification { UserId = null, Message = "pour les admins" });
        Assert.Single(_repository.GetNotifications(Alice, isAdmin: false));
        Assert.Equal(2, _repository.GetNotifications(Alice, isAdmin: true).Count);

        _repository.MarkNotificationsRead(Alice, isAdmin: false);
        var all = _repository.GetNotifications(Alice, isAdmin: true);
        Assert.NotNull(all.Single(n => n.UserId == Alice).ReadAt);
        Assert.Null(all.Single(n => n.UserId is null).ReadAt);
    }
}
