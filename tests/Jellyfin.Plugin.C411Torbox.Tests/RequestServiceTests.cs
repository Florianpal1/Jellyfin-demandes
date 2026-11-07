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

    private readonly ManualTime _time = new() { Now = DateTimeOffset.UtcNow };

    private bool _torboxFails;

    /// <summary>Si renseigné, createtorrent signale son arrivée puis attend ce signal (Torbox lent).</summary>
    private (ManualResetEventSlim Entered, ManualResetEventSlim Release)? _slowCreate;
    private string _myList = """{ "success": true, "data": [] }""";

    /// <summary>Contenu de /mylist une fois createtorrent appelé (simule un doublon détecté par Torbox).</summary>
    private string? _myListAfterCreate;

    public RequestServiceTests()
    {
        _repository = new RequestRepository(_dbPath);
        _http = new FakeHttp((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/createtorrent", StringComparison.Ordinal))
            {
                if (_slowCreate is { } slow)
                {
                    slow.Entered.Set();
                    slow.Release.Wait(TimeSpan.FromSeconds(10));
                }

                _myList = _myListAfterCreate ?? _myList;
                return _torboxFails
                    ? FakeHttp.Json("""{ "success": false, "detail": "Limite atteinte" }""", HttpStatusCode.Forbidden)
                    : FakeHttp.Json($$"""{ "success": true, "data": { "torrent_id": 99, "hash": "{{Hash}}" } }""");
            }

            if (path.EndsWith("/mylist", StringComparison.Ordinal))
            {
                return FakeHttp.Json(_myList);
            }

            if (path.EndsWith("/controltorrent", StringComparison.Ordinal))
            {
                return FakeHttp.Json("""{ "success": true }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        _service = new RequestService(
            _repository,
            _cache,
            new TorboxSubmitter(
                new IndexerProvider(new ProwlarrClient(_http, _config), new TorznabClient(_http, _config), _config),
                new TorboxClient(_http, _config),
                NullLogger<TorboxSubmitter>.Instance),
            new TorboxClient(_http, _config),
            _library,
            _notifications,
            _config,
            NullLogger<RequestService>.Instance,
            _time);
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

    private int ControlTorrentCalls => _http.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/controltorrent", StringComparison.Ordinal));

    private int CreateTorrentCalls => _http.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/createtorrent", StringComparison.Ordinal));

    private string AddResult(long sizeBytes, int seeders, bool cached = false, RequestScope scope = RequestScope.Movie, string? hash = Hash)
    {
        var context = scope == RequestScope.Movie
            ? new TorrentSearchRequest { Kind = MediaKind.Movie, Name = "Dune", Year = 2021, ProviderIds = new() { ["Imdb"] = "tt1160419" } }
            : scope == RequestScope.Book
            ? new TorrentSearchRequest { Kind = MediaKind.Book, Name = "Astérix le Gaulois", Year = 1961, ProviderIds = new() { ["GoogleBooks"] = "gb1", ["Isbn"] = "9782012101333" } }
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
    public async Task CachedTorrent_IsAutoApproved_EvenWithoutSeeders()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(10 * Gb, 0, cached: true), CancellationToken.None);

        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(AutoApprovalReason.Cached, r.AutoApprovalReason);
    }

    [Fact]
    public async Task HugeCachedTorrent_WaitsForAdmin()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(80 * Gb, 50, cached: true), CancellationToken.None);

        Assert.Equal(RequestStatus.Pending, r.Status);
        Assert.Equal(0, CreateTorrentCalls);
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
    public async Task TorrentAlreadyOnTorbox_IsAdopted_WithoutResending()
    {
        // Ajouté à la main sur Torbox (hors plugin), déjà téléchargé.
        _myList = $$"""{ "success": true, "data": [ { "id": 555, "hash": "{{Hash.ToUpperInvariant()}}", "name": "Dune", "progress": 1, "download_finished": true } ] }""";

        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);

        Assert.Equal(RequestStatus.CompletedOnTorbox, r.Status);
        Assert.Equal(555, r.TorboxTorrentId);
        Assert.Equal(1, r.TorboxProgress);
        Assert.Equal(0, CreateTorrentCalls);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.Contains("déjà sur Torbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TorboxRefusesDuplicate_RequestIsAttachedToExistingTorrent()
    {
        // La liste ne contient pas encore le torrent au moment de la vérification, Torbox refuse le doublon.
        _torboxFails = true;
        _myListAfterCreate = $$"""{ "success": true, "data": [ { "id": 777, "hash": "{{Hash}}", "name": "Dune", "progress": 0.3, "download_finished": false } ] }""";

        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);

        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(777, r.TorboxTorrentId);
        Assert.Equal(0.3, r.TorboxProgress);
        Assert.Null(r.LastError);
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
    public async Task RefreshNow_UpdatesStatuses_AndIgnoresRapidRepeats()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 1, "download_finished": true } ] }""";

        Assert.True(await _service.RefreshNowAsync(CancellationToken.None));
        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);

        // Deuxième clic dans les 30 s : ni Torbox ni la bibliothèque ne sont relus.
        _library.MovieInLibrary = true;
        var calls = _http.Calls.Count;
        Assert.False(await _service.RefreshNowAsync(CancellationToken.None));
        Assert.Equal(calls, _http.Calls.Count);
        Assert.Equal(RequestStatus.CompletedOnTorbox, _repository.Get(r.Id)!.Status);
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
    public async Task Book_UsesBookThreshold_KeepsItsIds_AndBecomesAvailable()
    {
        // Seuil livres : 2 Go par défaut.
        var big = await _service.CreateAsync(Alice, "alice", AddResult(3 * Gb, 50, scope: RequestScope.Book, hash: "b".PadRight(40, 'b')), CancellationToken.None);
        Assert.Equal(RequestStatus.Pending, big.Status);

        var r = await _service.CreateAsync(Alice, "alice", AddResult(200 * 1024 * 1024, 50, scope: RequestScope.Book), CancellationToken.None);
        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(MediaKind.Book, r.MediaKind);
        Assert.Equal(RequestScope.Book, r.Scope);
        Assert.Null(r.Season);

        var stored = _repository.Get(r.Id)!;
        Assert.Equal("GoogleBooks=gb1;Isbn=9782012101333", stored.BookIds);
        Assert.Equal("Astérix le Gaulois (1961)", RequestService.Describe(stored));

        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.SentToTorbox, _repository.Get(r.Id)!.Status);
        Assert.Equal("gb1", _library.LastBookCheck!.Value.Ids["GoogleBooks"]);
        Assert.Equal("Astérix le Gaulois", _library.LastBookCheck!.Value.Title);

        _library.BookAvailable = true;
        await _service.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Available, _repository.Get(r.Id)!.Status);
    }

    [Fact]
    public async Task Book_AlreadyInLibrary_IsRefused()
    {
        _library.BookInLibrary = true;

        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50, scope: RequestScope.Book), CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
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

    [Fact]
    public async Task SlowTorbox_DoesNotBlockOtherRequests()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _slowCreate = (entered, release);

        var sending = Task.Run(() => _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        // Pendant l'envoi : une autre demande et une validation passent sans attendre Torbox.
        var other = await _service.CreateAsync(Bob, "bob", AddResult(40 * Gb, 50, hash: "d".PadRight(40, 'd')), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RequestStatus.Pending, other.Status);
        await _service.RejectAsync(other.Id, "admin", null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Même torrent demandé pendant l'envoi : il suit la demande et passe « envoyé » avec elle.
        var follower = await _service.CreateAsync(Bob, "bob", AddResult(1 * Gb, 50), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RequestStatus.AutoApproved, follower.Status);

        release.Set();
        var r = await sending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RequestStatus.SentToTorbox, r.Status);
        Assert.Equal(RequestStatus.SentToTorbox, _repository.Get(follower.Id)!.Status);
        Assert.Equal(1, CreateTorrentCalls);
    }

    [Fact]
    public async Task ApproveTwice_SecondAdminGetsConflict()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _slowCreate = (entered, release);

        var first = Task.Run(() => _service.ApproveAsync(r.Id, "admin1", CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.ApproveAsync(r.Id, "admin2", CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);

        release.Set();
        Assert.Equal(RequestStatus.SentToTorbox, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(1, CreateTorrentCalls);
    }

    [Fact]
    public async Task TorrentMissingFromTorbox_FailsAfterGracePeriod()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        _myList = """{ "success": true, "data": [] }""";

        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        var missing = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.SentToTorbox, missing.Status);
        Assert.NotNull(missing.MissingOnTorboxSince);

        _time.Now += RequestService.MissingOnTorboxGracePeriod;
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        var failed = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Contains("n'est plus sur le compte Torbox", failed.LastError, StringComparison.Ordinal);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.Contains("n'est plus sur le compte Torbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TorrentBackOnTorbox_ClearsMissingFlag()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        _myList = """{ "success": true, "data": [] }""";
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);

        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 0.5, "download_state": "downloading" } ] }""";
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        _time.Now += RequestService.MissingOnTorboxGracePeriod * 2;
        await _service.RefreshTorboxStatusAsync(CancellationToken.None);

        var saved = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.DownloadingOnTorbox, saved.Status);
        Assert.Null(saved.MissingOnTorboxSince);
    }

    [Fact]
    public async Task TorboxErrorState_FailsRequest_AndResendReplacesTheTorrent()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 0.1, "download_state": "error" } ] }""";

        await _service.RefreshTorboxStatusAsync(CancellationToken.None);
        var failed = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Contains("erreur", failed.LastError, StringComparison.Ordinal);

        // Renvoyer : le torrent en erreur est supprimé de Torbox puis ajouté à nouveau.
        var resent = await _service.ApproveAsync(r.Id, "admin", CancellationToken.None);
        Assert.Equal(RequestStatus.SentToTorbox, resent.Status);
        Assert.Equal(1, ControlTorrentCalls);
        Assert.Equal(2, CreateTorrentCalls);
    }

    [Fact]
    public async Task StalledTorrent_StaysInProgress_WithItsState()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        _myList = $$"""{ "success": true, "data": [ { "id": 99, "hash": "{{Hash}}", "progress": 0.2, "download_state": "stalled (no seeds)" } ] }""";

        await _service.RefreshTorboxStatusAsync(CancellationToken.None);

        var saved = _repository.Get(r.Id)!;
        Assert.Equal(RequestStatus.DownloadingOnTorbox, saved.Status);
        Assert.Equal("Torbox : stalled (no seeds)", saved.LastError);
    }

    [Fact]
    public async Task User_CancelsOwnPendingRequest_ButNotSomeoneElses_NorASentOne()
    {
        var pending = await _service.CreateAsync(Alice, "alice", AddResult(40 * Gb, 50), CancellationToken.None);

        var notFound = await Assert.ThrowsAsync<RequestException>(() => _service.CancelAsync(pending.Id, Bob, "bob", false, CancellationToken.None));
        Assert.Equal(404, notFound.StatusCode);

        var cancelled = await _service.CancelAsync(pending.Id, Alice, "alice", false, CancellationToken.None);
        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        Assert.DoesNotContain(_repository.GetActive(), x => x.Id == pending.Id);

        var sent = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50, hash: "e".PadRight(40, 'e')), CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<RequestException>(() => _service.CancelAsync(sent.Id, Alice, "alice", false, CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task Admin_CancelsSentRequest_AndDeletesTorrentOnlyWhenUnused()
    {
        var a = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        var b = await _service.CreateAsync(Bob, "bob", AddResult(1 * Gb, 50), CancellationToken.None);

        // Bob suit toujours ce torrent : il n'est pas supprimé de Torbox.
        await _service.CancelAsync(a.Id, null, "admin", deleteFromTorbox: true, CancellationToken.None);
        Assert.Equal(0, ControlTorrentCalls);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.Contains("annulée par un administrateur", StringComparison.Ordinal));

        var cancelled = await _service.CancelAsync(b.Id, null, "admin", deleteFromTorbox: true, CancellationToken.None);
        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        Assert.Equal("admin", cancelled.ReviewedBy);
        Assert.Equal(1, ControlTorrentCalls);
    }

    [Fact]
    public async Task Admin_MarksAvailable_WhenJellyfinDoesNotRecognizeTheMedia()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);

        var available = await _service.MarkAvailableAsync(r.Id, "admin", CancellationToken.None);

        Assert.Equal(RequestStatus.Available, available.Status);
        Assert.Contains(_notifications.Sent, n => n.UserId == Alice && n.Message.Contains("disponible", StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<RequestException>(() => _service.MarkAvailableAsync(r.Id, "admin", CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task StaleRequest_IsReportedOnce()
    {
        var r = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        Assert.Empty(await _service.CheckStaleAsync(CancellationToken.None));

        _time.Now = DateTimeOffset.UtcNow.AddDays(_config.Current.StaleRequestDays + 1);
        var stale = await _service.CheckStaleAsync(CancellationToken.None);
        Assert.Equal(r.Id, Assert.Single(stale).Id);
        Assert.Contains(_notifications.Sent, n => n.UserId is null && n.Message.Contains("toujours « envoyée à Torbox »", StringComparison.Ordinal));

        Assert.Empty(await _service.CheckStaleAsync(CancellationToken.None));

        _config.Current.StaleRequestDays = 0;
        Assert.Empty(await _service.CheckStaleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Purge_RemovesOldFinishedRequestsAndReadNotifications_Only()
    {
        var available = await _service.CreateAsync(Alice, "alice", AddResult(1 * Gb, 50), CancellationToken.None);
        await _service.MarkAvailableAsync(available.Id, "admin", CancellationToken.None);
        var pending = await _service.CreateAsync(Bob, "bob", AddResult(40 * Gb, 50, hash: "f".PadRight(40, 'f')), CancellationToken.None);
        _repository.AddNotification(new Notification { UserId = Alice, Message = "lue" });
        _repository.MarkNotificationsRead(Alice, isAdmin: false);
        _repository.AddNotification(new Notification { UserId = Alice, Message = "non lue" });

        Assert.Equal((0, 0), _service.Purge());

        _time.Now = DateTimeOffset.UtcNow.AddDays(_config.Current.RequestRetentionDays + 1);
        var (requests, notifications) = _service.Purge();

        Assert.Equal(1, requests);
        Assert.True(notifications >= 1);
        Assert.Null(_repository.Get(available.Id));
        Assert.NotNull(_repository.Get(pending.Id));
        Assert.Contains(_repository.GetNotifications(Alice, isAdmin: false), n => n.Message == "non lue");
        Assert.DoesNotContain(_repository.GetNotifications(Alice, isAdmin: false), n => n.Message == "lue");
    }
}
