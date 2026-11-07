using System.Net;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.C411Torbox.Tests;

/// <summary>Webhook Discord : nom du demandeur, et option pour n'envoyer que les notifications des admins.</summary>
public sealed class NotificationServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "c411torbox-notif-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FakeConfig _config = new();
    private readonly RequestRepository _repository;
    private readonly FakeHttp _http = new((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        _config.Current.DiscordWebhookUrl = "https://discord.test/api/webhooks/1/abc";
        _repository = new RequestRepository(_dbPath);
        _service = new NotificationService(_repository, _http, _config, NullLogger<NotificationService>.Instance);
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

    private MediaRequest AddRequest(Guid userId) => _repository.Insert(new MediaRequest
    {
        UserId = userId,
        UserName = "alice",
        MediaTitle = "Dune",
        TorrentTitle = "t",
        Status = RequestStatus.Pending,
    });

    [Fact]
    public async Task UserNotification_IsSentWithTheRequesterName()
    {
        var alice = Guid.NewGuid();
        var r = AddRequest(alice);

        await _service.NotifyUserAsync(alice, "Votre demande est validée.", r.Id, CancellationToken.None);

        var call = Assert.Single(_http.Calls);
        Assert.Contains("👤 alice — Votre demande est validée.", System.Text.Json.JsonDocument.Parse(call.Body).RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Single(_repository.GetNotifications(alice, isAdmin: false));
    }

    [Fact]
    public async Task UserNotifications_CanStayOffDiscord()
    {
        _config.Current.DiscordSendUserNotifications = false;
        var alice = Guid.NewGuid();
        var r = AddRequest(alice);

        await _service.NotifyUserAsync(alice, "Votre demande est validée.", r.Id, CancellationToken.None);
        await _service.NotifyAdminsAsync("Nouvelle demande à valider.", r.Id, CancellationToken.None);

        // Toujours visible dans la page ; seule la notification des admins part sur Discord.
        Assert.Single(_repository.GetNotifications(alice, isAdmin: false));
        var call = Assert.Single(_http.Calls);
        Assert.StartsWith("🛡️ Nouvelle demande", System.Text.Json.JsonDocument.Parse(call.Body).RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
    }
}
