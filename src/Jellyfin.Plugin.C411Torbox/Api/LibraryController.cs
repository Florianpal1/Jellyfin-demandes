using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Api;

/// <summary>Gestion des médias de la bibliothèque (liste, suppression, remplacement par un autre torrent) : administrateurs seulement.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("C411Torbox/Admin/Library")]
public class LibraryController : C411TorboxControllerBase
{
    private const int MaxPageSize = 100;

    private readonly IMediaLibrary _media;
    private readonly TorrentSearchService _search;
    private readonly RequestService _requests;
    private readonly RequestRepository _repository;
    private readonly SearchRateLimiter _rateLimiter;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(
        IMediaLibrary media,
        TorrentSearchService search,
        RequestService requests,
        RequestRepository repository,
        SearchRateLimiter rateLimiter,
        ILogger<LibraryController> logger)
    {
        _media = media;
        _search = search;
        _requests = requests;
        _repository = repository;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>Films, séries ou livres de la bibliothèque, page par page.</summary>
    [HttpGet]
    public ActionResult Browse(
        [FromQuery] LibraryItemType type = LibraryItemType.Movie,
        [FromQuery] string? search = null,
        [FromQuery] LibrarySort sort = LibrarySort.Recent,
        [FromQuery] int start = 0,
        [FromQuery] int limit = 50)
    {
        if (type is not (LibraryItemType.Movie or LibraryItemType.Series or LibraryItemType.Book) || search?.Length > 200)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, "Recherche invalide.");
        }

        var page = _media.Browse(type, search, sort, Math.Max(0, start), Math.Clamp(limit, 1, MaxPageSize));
        MarkReplacing(page.Items);
        return JsonOk(page);
    }

    /// <summary>Saisons d'une série et leurs épisodes.</summary>
    [HttpGet("{id}/Seasons")]
    public ActionResult Seasons([FromRoute] string id)
    {
        var seasons = Guid.TryParse(id, out var guid) ? _media.GetSeasons(guid) : null;
        if (seasons is null)
        {
            return ErrorResult(StatusCodes.Status404NotFound, "Série introuvable.");
        }

        MarkReplacing(seasons);
        return JsonOk(seasons);
    }

    /// <summary>Torrents possibles pour remplacer un film, une saison, un épisode ou un livre.</summary>
    [HttpPost("{id}/Alternatives")]
    public async Task<ActionResult> Alternatives([FromRoute] string id, CancellationToken ct)
    {
        var media = Guid.TryParse(id, out var guid) ? _media.Get(guid) : null;
        if (media is null)
        {
            return ErrorResult(StatusCodes.Status404NotFound, "Média introuvable : il a peut-être été supprimé.");
        }

        if (!media.CanReplace)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, "Une série se remplace saison par saison ou épisode par épisode.");
        }

        if (!_rateLimiter.TryAcquire(CurrentUserId))
        {
            return ErrorResult(StatusCodes.Status429TooManyRequests, "Trop de recherches : patientez une minute.");
        }

        try
        {
            return JsonOk(await _search.SearchAsync(media.ToSearch(), ct).ConfigureAwait(false));
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
            _logger.LogWarning(ex, "Recherche de torrents de remplacement en échec");
            return ErrorResult(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ErrorResult(StatusCodes.Status504GatewayTimeout, "Prowlarr ou Torbox n'a pas répondu à temps.");
        }
    }

    /// <summary>Remplace le média par un résultat de <see cref="Alternatives"/> (validé d'office, envoyé à Torbox).</summary>
    [HttpPost("{id}/Replace")]
    public async Task<ActionResult> Replace([FromRoute] string id, [FromBody] CreateRequestBody body, CancellationToken ct)
    {
        if (!IsRealUser)
        {
            return ErrorResult(StatusCodes.Status403Forbidden, "Un remplacement doit être lancé par un utilisateur connecté.");
        }

        var media = Guid.TryParse(id, out var guid) ? _media.Get(guid) : null;
        if (media is null)
        {
            return ErrorResult(StatusCodes.Status404NotFound, "Média introuvable : il a peut-être été supprimé.");
        }

        try
        {
            return JsonOk(await _requests.CreateReplacementAsync(CurrentUserId, CurrentUserName, media, body.ResultId, ct).ConfigureAwait(false));
        }
        catch (RequestException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>Supprime le média de Jellyfin et ses fichiers du disque.</summary>
    [HttpDelete("{id}")]
    public ActionResult Delete([FromRoute] string id)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return ErrorResult(StatusCodes.Status404NotFound, "Média introuvable.");
        }

        var itemId = guid.ToString("N");
        if (_repository.GetActive().Any(r => r.ReplaceItemId == itemId && r.Status == RequestStatus.Importing))
        {
            return ErrorResult(StatusCodes.Status409Conflict, "Son remplacement est en cours de téléchargement vers le NAS : attendez la fin.");
        }

        try
        {
            var name = _media.Delete(guid);
            if (name is null)
            {
                return ErrorResult(StatusCodes.Status404NotFound, "Média introuvable : il a peut-être déjà été supprimé.");
            }

            _logger.LogInformation("Média « {Name} » ({Id}) supprimé par {Admin}", name, itemId, CurrentUserName);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResult(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Suppression du média {Id} impossible", itemId);
            return ErrorResult(StatusCodes.Status500InternalServerError, "Suppression impossible : " + ex.Message);
        }
    }

    /// <summary>Signale les médias (et épisodes des saisons) dont un remplacement est en cours.</summary>
    private void MarkReplacing(IEnumerable<LibraryItem> items)
    {
        var replacing = _repository.GetActive()
            .Select(r => r.ReplaceItemId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (replacing.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            item.Replacing = replacing.Contains(item.Id);
            foreach (var episode in item.Episodes ?? [])
            {
                episode.Replacing = item.Replacing || replacing.Contains(episode.Id);
            }
        }
    }
}
