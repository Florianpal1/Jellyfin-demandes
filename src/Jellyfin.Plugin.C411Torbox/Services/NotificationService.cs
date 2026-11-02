using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

public interface INotificationService
{
    Task NotifyAdminsAsync(string message, long? requestId, CancellationToken ct);

    Task NotifyUserAsync(Guid userId, string message, long? requestId, CancellationToken ct);
}

/// <summary>Notifications stockées en base (affichées dans la page du plugin) + webhook Discord optionnel.</summary>
internal sealed class NotificationService : INotificationService
{
    public const string HttpClientName = "C411Torbox.Webhook";

    private readonly RequestRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _config;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(RequestRepository repository, IHttpClientFactory httpClientFactory, IConfigProvider config, ILogger<NotificationService> logger)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public Task NotifyAdminsAsync(string message, long? requestId, CancellationToken ct)
    {
        _repository.AddNotification(new Notification { UserId = null, RequestId = requestId, Message = message });
        return SendWebhookAsync("🛡️ " + message, ct);
    }

    public Task NotifyUserAsync(Guid userId, string message, long? requestId, CancellationToken ct)
    {
        _repository.AddNotification(new Notification { UserId = userId, RequestId = requestId, Message = message });
        return SendWebhookAsync(message, ct);
    }

    private async Task SendWebhookAsync(string message, CancellationToken ct)
    {
        var url = _config.Current.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(uri, new { content = message }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook de notification : HTTP {Status}", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            // Une notification ratée ne doit jamais faire échouer une demande.
            _logger.LogWarning(ex, "Webhook de notification injoignable");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Webhook de notification : délai dépassé");
        }
    }
}
