using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Jellyfin.Plugin.C411Torbox.Services.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Api;

/// <summary>Endpoints accessibles à tout utilisateur Jellyfin connecté.</summary>
[ApiController]
[Authorize]
[Route("C411Torbox")]
public class UserController : C411TorboxControllerBase
{
    private readonly MetadataSearchService _metadata;
    private readonly TorrentSearchService _search;
    private readonly RequestService _requests;
    private readonly RequestRepository _repository;
    private readonly SearchRateLimiter _rateLimiter;
    private readonly IEpisodeTitleProvider _episodes;
    private readonly ILibraryLookup _library;
    private readonly ILogger<UserController> _logger;

    public UserController(
        MetadataSearchService metadata,
        TorrentSearchService search,
        RequestService requests,
        RequestRepository repository,
        SearchRateLimiter rateLimiter,
        IEpisodeTitleProvider episodes,
        ILibraryLookup library,
        ILogger<UserController> logger)
    {
        _metadata = metadata;
        _search = search;
        _requests = requests;
        _repository = repository;
        _rateLimiter = rateLimiter;
        _episodes = episodes;
        _library = library;
        _logger = logger;
    }

    [HttpGet("Me")]
    public ActionResult Me()
        => JsonOk(new CurrentUserInfo { UserId = CurrentUserId, Name = CurrentUserName, IsAdmin = IsAdmin });

    /// <summary>Recherche de fiches films / séries / livres.</summary>
    [HttpGet("Media/Search")]
    public async Task<ActionResult> SearchMedia([FromQuery, Required] string query, [FromQuery] MediaKind kind, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, "Recherche invalide.");
        }

        if (year is < 1800 or > 2200)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, "Année invalide.");
        }

        return JsonOk(await _metadata.SearchAsync(query.Trim(), kind, year, ct).ConfigureAwait(false));
    }

    /// <summary>Épisodes d'une saison d'une série (fiche du cache serveur), avec ceux déjà présents dans la bibliothèque.</summary>
    [HttpGet("Media/Episodes")]
    public async Task<ActionResult> SeasonEpisodes([FromQuery, Required] string mediaId, [FromQuery] int season, CancellationToken ct)
    {
        var media = _metadata.Get(mediaId ?? string.Empty);
        if (media is null)
        {
            return ErrorResult(StatusCodes.Status410Gone, "Cette fiche a expiré : relancez la recherche du titre.");
        }

        if (media.Kind != MediaKind.Series || season is < 0 or > 200)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, "Saison invalide.");
        }

        var ids = LibraryLookup.FilterIds(media.ProviderIds);
        var episodes = await _episodes.GetSeasonEpisodesAsync(ids, season, ct).ConfigureAwait(false);
        var present = _library.GetEpisodeNumbers(ids, season);
        return JsonOk(episodes.Select(e => new EpisodeListItem { Episode = e.Episode, Title = e.Title, InLibrary = present.Contains(e.Episode) }));
    }

    /// <summary>Recherche des torrents C411 pour un média (film, saison, épisode ou livre).</summary>
    [HttpPost("Torrents/Search")]
    public async Task<ActionResult> SearchTorrents([FromBody] TorrentSearchBody body, CancellationToken ct)
    {
        // Fiche relue côté serveur : titre, identifiants et type ne sont jamais fournis par le navigateur.
        var media = _metadata.Get(body.MediaId ?? string.Empty);
        if (media is null)
        {
            return ErrorResult(StatusCodes.Status410Gone, "Cette fiche a expiré : relancez la recherche du titre.");
        }

        if (!_rateLimiter.TryAcquire(CurrentUserId))
        {
            return ErrorResult(StatusCodes.Status429TooManyRequests, "Trop de recherches : patientez une minute.");
        }

        try
        {
            return JsonOk(await _search.SearchAsync(TorrentSearchRequest.From(media, body.Season, body.Episode), ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResult(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Recherche de torrents en échec");
            return ErrorResult(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ErrorResult(StatusCodes.Status504GatewayTimeout, "Prowlarr ou Torbox n'a pas répondu à temps.");
        }
    }

    [HttpPost("Requests")]
    public async Task<ActionResult> CreateRequest([FromBody] CreateRequestBody body, CancellationToken ct)
    {
        if (!IsRealUser)
        {
            return ErrorResult(StatusCodes.Status403Forbidden, "Une demande doit être faite par un utilisateur connecté.");
        }

        try
        {
            var request = await _requests.CreateAsync(CurrentUserId, CurrentUserName, body.ResultId, ct).ConfigureAwait(false);
            return JsonOk(request);
        }
        catch (RequestException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message);
        }
        catch (Exception ex) when (ex is TorboxException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Création de demande en échec");
            return ErrorResult(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>Annule une de mes demandes tant qu'elle n'est pas partie sur Torbox (en attente ou en échec).</summary>
    [HttpPost("Requests/{id}/Cancel")]
    public async Task<ActionResult> CancelMyRequest([FromRoute] long id, CancellationToken ct)
    {
        if (!IsRealUser)
        {
            return ErrorResult(StatusCodes.Status403Forbidden, "Une demande doit être annulée par un utilisateur connecté.");
        }

        try
        {
            return JsonOk(await _requests.CancelAsync(id, CurrentUserId, CurrentUserName, deleteFromTorbox: false, ct).ConfigureAwait(false));
        }
        catch (RequestException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message);
        }
    }

    [HttpGet("Requests/Mine")]
    public ActionResult MyRequests() => JsonOk(_repository.GetByUser(CurrentUserId));

    /// <summary>Met à jour les statuts (Torbox, bibliothèque) sans attendre la tâche planifiée, puis renvoie mes demandes.</summary>
    [HttpPost("Requests/Mine/Refresh")]
    public async Task<ActionResult> RefreshMyRequests(CancellationToken ct)
    {
        await _requests.RefreshNowAsync(ct).ConfigureAwait(false);
        return JsonOk(_repository.GetByUser(CurrentUserId));
    }

    [HttpGet("Notifications")]
    public ActionResult Notifications() => JsonOk(_repository.GetNotifications(CurrentUserId, IsAdmin));

    [HttpPost("Notifications/Read")]
    public ActionResult MarkNotificationsRead()
    {
        _repository.MarkNotificationsRead(CurrentUserId, IsAdmin);
        return NoContent();
    }
}
