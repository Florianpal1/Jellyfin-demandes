using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.C411Torbox.Api;

/// <summary>Endpoints réservés aux administrateurs Jellyfin.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("C411Torbox/Admin")]
public class AdminController : C411TorboxControllerBase
{
    private readonly RequestService _requests;
    private readonly RequestRepository _repository;
    private readonly ProwlarrClient _prowlarr;
    private readonly TorboxClient _torbox;

    public AdminController(RequestService requests, RequestRepository repository, ProwlarrClient prowlarr, TorboxClient torbox)
    {
        _requests = requests;
        _repository = repository;
        _prowlarr = prowlarr;
        _torbox = torbox;
    }

    [HttpGet("Requests")]
    public ActionResult GetRequests([FromQuery] RequestStatus? status) => JsonOk(_repository.GetAll(status));

    [HttpPost("Requests/{id}/Approve")]
    public Task<ActionResult> Approve([FromRoute] long id, CancellationToken ct)
        => Run(() => _requests.ApproveAsync(id, CurrentUserName, ct));

    [HttpPost("Requests/{id}/Reject")]
    public Task<ActionResult> Reject([FromRoute] long id, [FromBody] RejectBody body, CancellationToken ct)
        => Run(() => _requests.RejectAsync(id, CurrentUserName, body.Reason, ct));

    /// <summary>Teste Prowlarr et renvoie la liste de ses indexeurs (pour choisir C411).</summary>
    [HttpGet("Test/Prowlarr")]
    public async Task<ActionResult> TestProwlarr(CancellationToken ct)
    {
        try
        {
            var indexers = await _prowlarr.GetIndexersAsync(ct).ConfigureAwait(false);
            return JsonOk(new
            {
                success = true,
                message = Invariant($"Connexion réussie : {indexers.Count} indexeur(s)."),
                indexers = indexers.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            return JsonOk(new { success = false, message = "Échec : " + ex.Message });
        }
    }

    [HttpGet("Test/Torbox")]
    public async Task<ActionResult> TestTorbox(CancellationToken ct)
    {
        try
        {
            await _torbox.TestAsync(ct).ConfigureAwait(false);
            return JsonOk(new ConnectionTestResult { Success = true, Message = "Connexion à Torbox réussie." });
        }
        catch (Exception ex) when (ex is TorboxException or TaskCanceledException)
        {
            return JsonOk(new ConnectionTestResult { Success = false, Message = "Échec : " + ex.Message });
        }
    }

    private static async Task<ActionResult> Run(Func<Task<MediaRequest>> action)
    {
        try
        {
            return JsonOk(await action().ConfigureAwait(false));
        }
        catch (RequestException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message);
        }
    }
}
