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
using Jellyfin.Plugin.C411Torbox.Services.Import;
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
    private readonly TorznabClient _torznab;
    private readonly TorboxClient _torbox;
    private readonly ImportService _import;
    private readonly ILibraryLookup _library;

    public AdminController(
        RequestService requests,
        RequestRepository repository,
        ProwlarrClient prowlarr,
        TorznabClient torznab,
        TorboxClient torbox,
        ImportService import,
        ILibraryLookup library)
    {
        _requests = requests;
        _repository = repository;
        _prowlarr = prowlarr;
        _torznab = torznab;
        _torbox = torbox;
        _import = import;
        _library = library;
    }

    /// <summary>Teste l'accès direct à C411 (Torznab) : adresse, certificat et clé API.</summary>
    [HttpGet("Test/Torznab")]
    public async Task<ActionResult> TestTorznab(CancellationToken ct)
    {
        try
        {
            var message = await _torznab.TestAsync(ct).ConfigureAwait(false);
            return JsonOk(new ConnectionTestResult { Success = true, Message = message });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or UriFormatException)
        {
            return JsonOk(new ConnectionTestResult { Success = false, Message = "Échec : " + ex.Message });
        }
    }

    [HttpGet("Requests")]
    public ActionResult GetRequests([FromQuery] RequestStatus? status) => JsonOk(_repository.GetAll(status));

    [HttpPost("Requests/{id}/Approve")]
    public Task<ActionResult> Approve([FromRoute] long id, CancellationToken ct)
        => Run(() => _requests.ApproveAsync(id, CurrentUserName, ct));

    [HttpPost("Requests/{id}/Reject")]
    public Task<ActionResult> Reject([FromRoute] long id, [FromBody] RejectBody body, CancellationToken ct)
        => Run(() => _requests.RejectAsync(id, CurrentUserName, body.Reason, ct));

    [HttpPost("Requests/{id}/RetryImport")]
    public Task<ActionResult> RetryImport([FromRoute] long id, CancellationToken ct)
        => Run(() => _requests.RetryImportAsync(id, ct));

    /// <summary>Vérifie les dossiers de rangement (existence et droit d'écriture) et liste les bibliothèques Jellyfin.</summary>
    [HttpGet("Test/Import")]
    public ActionResult TestImport()
    {
        var lines = new List<string>();
        bool ok = true;
        foreach (var (kind, label) in new[] { (MediaKind.Movie, "Films"), (MediaKind.Series, "Séries"), (MediaKind.Book, "Livres") })
        {
            var root = _import.ResolveRoot(kind);
            var check = CheckFolder(root);

            // Les livres sont facultatifs : sans dossier ni bibliothèque Livres, seul leur rangement échouera.
            ok &= check is null || (kind == MediaKind.Book && root is null);
            lines.Add($"{label} : {(root ?? "aucun dossier")} — {check ?? "OK"}");
        }

        return JsonOk(new
        {
            success = ok,
            message = string.Join(" | ", lines),
            movieLibraries = _library.GetLibraryFolders(MediaKind.Movie),
            seriesLibraries = _library.GetLibraryFolders(MediaKind.Series),
            bookLibraries = _library.GetLibraryFolders(MediaKind.Book),
        });
    }

    /// <summary>Null si le dossier existe et est accessible en écriture, sinon la raison.</summary>
    private static string? CheckFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "à renseigner (aucune bibliothèque Jellyfin de ce type)";
        }

        if (!Directory.Exists(path))
        {
            return "introuvable depuis le serveur Jellyfin";
        }

        var probe = Path.Combine(path, ".c411torbox-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "pas de droit d'écriture pour Jellyfin (" + ex.Message + ")";
        }
    }

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
