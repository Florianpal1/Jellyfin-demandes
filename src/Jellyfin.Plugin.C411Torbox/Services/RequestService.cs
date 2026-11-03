using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Erreur fonctionnelle à afficher telle quelle à l'utilisateur.</summary>
public class RequestException : Exception
{
    public RequestException(string message, int statusCode = 400)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>Cycle de vie des demandes : création, auto-validation, validation admin, envoi Torbox, suivi.</summary>
public class RequestService
{
    /// <summary>Statuts où le média peut apparaître dans la bibliothèque. Pas « Importing » : l'import en cours finit d'abord.</summary>
    private static readonly RequestStatus[] SentStatuses =
    [
        RequestStatus.SentToTorbox,
        RequestStatus.DownloadingOnTorbox,
        RequestStatus.CompletedOnTorbox,
        RequestStatus.Imported,
        RequestStatus.ImportFailed,
    ];

    private readonly RequestRepository _repository;
    private readonly SearchResultCache _cache;
    private readonly IndexerProvider _indexers;
    private readonly TorboxClient _torbox;
    private readonly ILibraryLookup _library;
    private readonly INotificationService _notifications;
    private readonly IConfigProvider _config;
    private readonly ILogger<RequestService> _logger;

    // Sérialise les transitions d'état : évite par exemple un double envoi si deux admins valident en même temps.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly object _manualRefreshGate = new();
    private DateTime _lastManualRefresh = DateTime.MinValue;

    /// <summary>Délai minimal entre deux actualisations manuelles (tous utilisateurs confondus) : ménage l'API Torbox.</summary>
    public static readonly TimeSpan ManualRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Actualisation demandée depuis l'interface (bouton « Actualiser ») : même travail que la tâche planifiée
    /// (progression Torbox puis arrivée dans la bibliothèque), au plus une fois toutes les 30 s.
    /// Renvoie false si l'actualisation a été ignorée (trop rapprochée) ; Torbox injoignable n'empêche pas la vérification de la bibliothèque.
    /// </summary>
    public async Task<bool> RefreshNowAsync(CancellationToken ct)
    {
        lock (_manualRefreshGate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastManualRefresh < ManualRefreshInterval)
            {
                return false;
            }

            _lastManualRefresh = now;
        }

        try
        {
            await RefreshTorboxStatusAsync(ct).ConfigureAwait(false);
        }
        catch (TorboxException ex)
        {
            _logger.LogWarning(ex, "Actualisation manuelle : lecture des statuts Torbox impossible");
        }

        await CheckAvailabilityAsync(ct).ConfigureAwait(false);
        return true;
    }

    public RequestService(
        RequestRepository repository,
        SearchResultCache cache,
        IndexerProvider indexers,
        TorboxClient torbox,
        ILibraryLookup library,
        INotificationService notifications,
        IConfigProvider config,
        ILogger<RequestService> logger)
    {
        _repository = repository;
        _cache = cache;
        _indexers = indexers;
        _torbox = torbox;
        _library = library;
        _notifications = notifications;
        _config = config;
        _logger = logger;
    }

    public static string Describe(MediaRequest r) => r.Scope switch
    {
        RequestScope.Movie or RequestScope.Book => r.Year.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} ({r.Year})") : r.MediaTitle,
        RequestScope.Episode => string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} S{r.Season:00}E{r.Episode:00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} saison {r.Season}"),
    };

    public async Task<MediaRequest> CreateAsync(Guid userId, string userName, string resultId, CancellationToken ct)
    {
        var entry = _cache.Get(resultId)
            ?? throw new RequestException("Ce résultat a expiré : relancez la recherche.", 410);
        var ctx = entry.Context;
        var res = entry.Result;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = LibraryLookup.FilterIds(ctx.ProviderIds);
            var bookIds = LibraryLookup.FilterBookIds(ctx.ProviderIds);
            int episodesAtRequest = 0;
            switch (ctx.Scope)
            {
                case RequestScope.Movie when _library.IsMovieInLibrary(ids):
                case RequestScope.Episode when _library.CountEpisodes(ids, ctx.Season!.Value, ctx.Episode) > 0:
                case RequestScope.Book when _library.IsBookInLibrary(bookIds):
                    throw new RequestException("Ce média est déjà dans la bibliothèque.", 409);
                case RequestScope.Season:
                    episodesAtRequest = _library.CountEpisodes(ids, ctx.Season!.Value, null);
                    break;
            }

            var hash = res.InfoHash ?? (entry.Source?.File is { } f ? TorrentHash.FromTorrentFile(f) : null);
            var siblings = hash is null ? new List<MediaRequest>() : _repository.GetActiveByHash(hash).ToList();
            var mine = siblings.FirstOrDefault(s => s.UserId == userId);
            if (mine is not null)
            {
                return mine;
            }

            var reason = ApprovalRules.Evaluate(ctx.Scope, res.SizeBytes, res.Seeders, res.IsCachedOnTorbox, _config.Current);
            var request = new MediaRequest
            {
                UserId = userId,
                UserName = userName,
                MediaKind = ctx.Kind,
                Scope = ctx.Scope,
                MediaTitle = ctx.Name,
                Year = ctx.Year,
                TmdbId = ids.GetValueOrDefault("Tmdb"),
                ImdbId = ids.GetValueOrDefault("Imdb"),
                TvdbId = ids.GetValueOrDefault("Tvdb"),
                BookIds = FormatBookIds(bookIds),
                PosterUrl = ctx.ImageUrl,
                Season = ctx.Season,
                Episode = ctx.Episode,
                TorrentTitle = res.Title,
                SizeBytes = res.SizeBytes,
                Seeders = res.Seeders,
                Leechers = res.Leechers,
                InfoHash = hash,
                ProwlarrGuid = res.Guid,
                MagnetUrl = res.MagnetUrl ?? entry.Source?.Magnet,
                DownloadUrl = res.DownloadUrl,
                WasCachedOnTorbox = res.IsCachedOnTorbox,
                AutoApprovalReason = reason,
                EpisodesInLibraryAtRequest = episodesAtRequest,
            };

            // Même torrent déjà demandé par quelqu'un d'autre : on suit la demande existante.
            var sibling = siblings.FirstOrDefault();
            if (sibling is not null)
            {
                request.Status = sibling.Status;
                request.AutoApprovalReason = sibling.AutoApprovalReason;
                request.TorboxTorrentId = sibling.TorboxTorrentId;
                request.TorboxProgress = sibling.TorboxProgress;
                _repository.Insert(request);
                await _notifications.NotifyUserAsync(userId, $"« {Describe(request)} » a déjà été demandé : votre demande suit la demande existante.", request.Id, ct).ConfigureAwait(false);
                return request;
            }

            if (reason == AutoApprovalReason.None)
            {
                request.Status = RequestStatus.Pending;
                _repository.Insert(request);
                await _notifications.NotifyAdminsAsync($"Nouvelle demande à valider : « {Describe(request)} » par {userName} ({FormatSize(request.SizeBytes)}, {request.Seeders} seeders).", request.Id, ct).ConfigureAwait(false);
                return request;
            }

            request.Status = RequestStatus.AutoApproved;
            _repository.Insert(request);
            var why = reason == AutoApprovalReason.Cached ? "déjà en cache Torbox" : "sous le seuil de taille";
            await _notifications.NotifyAdminsAsync($"Demande auto-validée ({why}) : « {Describe(request)} » par {userName}.", request.Id, ct).ConfigureAwait(false);
            // Pas d'annulation : une déconnexion du navigateur ne doit pas laisser la demande à moitié envoyée.
            await SendToTorboxAsync(request, entry.Source, CancellationToken.None).ConfigureAwait(false);
            return request;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MediaRequest> ApproveAsync(long id, string adminName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (request.Status is not (RequestStatus.Pending or RequestStatus.Failed))
            {
                throw new RequestException("Seule une demande en attente ou en échec peut être validée.", 409);
            }

            request.Status = RequestStatus.Approved;
            request.ReviewedBy = adminName;
            request.ReviewedAt = DateTime.UtcNow;
            request.LastError = null;
            _repository.Update(request);
            await SendToTorboxAsync(request, null, CancellationToken.None).ConfigureAwait(false);
            return request;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MediaRequest> RejectAsync(long id, string adminName, string? reason, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (request.Status is not (RequestStatus.Pending or RequestStatus.Failed))
            {
                throw new RequestException("Seule une demande en attente ou en échec peut être refusée.", 409);
            }

            var targets = SameTorrent(request, RequestStatus.Pending, RequestStatus.Failed);
            var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            foreach (var r in targets)
            {
                r.Status = RequestStatus.Rejected;
                r.ReviewedBy = adminName;
                r.ReviewedAt = DateTime.UtcNow;
                r.RejectReason = cleanReason;
                _repository.Update(r);
                var msg = $"Votre demande « {Describe(r)} » a été refusée" + (cleanReason is null ? "." : $" : {cleanReason}");
                await _notifications.NotifyUserAsync(r.UserId, msg, r.Id, ct).ConfigureAwait(false);
            }

            return request;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Relance le téléchargement vers le NAS d'une demande dont l'import a échoué (et des demandes du même torrent).</summary>
    public async Task<MediaRequest> RetryImportAsync(long id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (request.Status != RequestStatus.ImportFailed)
            {
                throw new RequestException("Seul un rangement en échec peut être relancé.", 409);
            }

            foreach (var r in SameTorrent(request, RequestStatus.ImportFailed))
            {
                r.Status = RequestStatus.CompletedOnTorbox;
                r.LastError = null;
                r.ImportProgress = null;
                _repository.Update(r);
            }

            return _repository.Get(id)!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Applique <paramref name="apply"/> (sous le verrou, sur des données relues) aux demandes actives qui vérifient
    /// <paramref name="filter"/>. Renvoie les demandes modifiées.
    /// </summary>
    internal async Task<IReadOnlyList<MediaRequest>> UpdateActiveAsync(Func<MediaRequest, bool> filter, Action<MediaRequest> apply, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = _repository.GetActive().Where(filter).ToList();
            foreach (var r in changed)
            {
                apply(r);
                _repository.Update(r);
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Au démarrage : un import interrompu par un arrêt du serveur repart (le fichier partiel est repris).</summary>
    public Task ResetInterruptedImportsAsync(CancellationToken ct)
        => UpdateActiveAsync(r => r.Status == RequestStatus.Importing, r => r.Status = RequestStatus.CompletedOnTorbox, ct);

    /// <summary>Met à jour la progression Torbox des demandes envoyées.</summary>
    public async Task RefreshTorboxStatusAsync(CancellationToken ct)
    {
        var active = _repository.GetActive()
            .Where(r => r.Status is RequestStatus.SentToTorbox or RequestStatus.DownloadingOnTorbox)
            .ToList();
        if (active.Count == 0)
        {
            return;
        }

        var torrents = await _torbox.GetMyListAsync(ct).ConfigureAwait(false);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var r in active)
            {
                var t = torrents.FirstOrDefault(t => r.TorboxTorrentId.HasValue && t.Id == r.TorboxTorrentId.Value)
                    ?? torrents.FirstOrDefault(t => r.InfoHash is not null && string.Equals(t.Hash, r.InfoHash, StringComparison.OrdinalIgnoreCase));
                if (t is null)
                {
                    continue;
                }

                var current = _repository.Get(r.Id);
                if (current is null || current.Status is not (RequestStatus.SentToTorbox or RequestStatus.DownloadingOnTorbox))
                {
                    continue;
                }

                current.TorboxTorrentId ??= t.Id;
                current.TorboxProgress = Math.Clamp(t.Progress, 0, 1);
                current.Status = t.DownloadFinished || t.Progress >= 1
                    ? RequestStatus.CompletedOnTorbox
                    : RequestStatus.DownloadingOnTorbox;
                _repository.Update(current);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Passe en « Disponible » les demandes dont le média est arrivé dans la bibliothèque.</summary>
    public async Task CheckAvailabilityAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var r in _repository.GetActive().Where(r => SentStatuses.Contains(r.Status)))
            {
                var ids = ProviderIdsOf(r);
                bool available = r.Scope switch
                {
                    RequestScope.Movie => _library.IsMovieInLibrary(ids),
                    RequestScope.Episode => _library.CountEpisodes(ids, r.Season ?? 0, r.Episode) > 0,
                    RequestScope.Book => _library.IsBookAvailable(ids, r.MediaTitle, r.ImportedPath, r.CreatedAt),
                    _ => _library.CountEpisodes(ids, r.Season ?? 0, null) > r.EpisodesInLibraryAtRequest,
                };
                if (!available)
                {
                    continue;
                }

                r.Status = RequestStatus.Available;
                r.TorboxProgress = 1;
                _repository.Update(r);
                await _notifications.NotifyUserAsync(r.UserId, $"« {Describe(r)} » est disponible dans Jellyfin.", r.Id, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static Dictionary<string, string> ProviderIdsOf(MediaRequest r)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(r.TmdbId))
        {
            ids["Tmdb"] = r.TmdbId;
        }

        if (!string.IsNullOrEmpty(r.ImdbId))
        {
            ids["Imdb"] = r.ImdbId;
        }

        if (!string.IsNullOrEmpty(r.TvdbId))
        {
            ids["Tvdb"] = r.TvdbId;
        }

        foreach (var pair in (r.BookIds ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=', StringComparison.Ordinal);
            if (i > 0 && i < pair.Length - 1)
            {
                ids[pair[..i]] = pair[(i + 1)..];
            }
        }

        return ids;
    }

    /// <summary>« GoogleBooks=abc;Isbn=978… » (null sans identifiant).</summary>
    internal static string? FormatBookIds(IReadOnlyDictionary<string, string> bookIds)
    {
        var text = string.Join(";", bookIds
            .Where(kv => !kv.Value.Contains(';', StringComparison.Ordinal) && !kv.Value.Contains('=', StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value));
        return text.Length == 0 ? null : text;
    }

    internal static string FormatSize(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.GetCultureInfo("fr-FR")) + " Go"
            : (bytes / (1024d * 1024)).ToString("0", CultureInfo.GetCultureInfo("fr-FR")) + " Mo";

    /// <summary>La demande et les autres demandes actives du même torrent dans l'un des statuts donnés.</summary>
    private List<MediaRequest> SameTorrent(MediaRequest request, params RequestStatus[] statuses)
    {
        var list = new List<MediaRequest> { request };
        if (request.InfoHash is not null)
        {
            list.AddRange(_repository.GetActiveByHash(request.InfoHash)
                .Where(r => r.Id != request.Id && statuses.Contains(r.Status)));
            if (statuses.Contains(RequestStatus.Failed))
            {
                list.AddRange(_repository.GetAll(RequestStatus.Failed)
                    .Where(r => r.Id != request.Id && string.Equals(r.InfoHash, request.InfoHash, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return list.DistinctBy(r => r.Id).ToList();
    }

    /// <summary>Torrent du compte Torbox ayant cet infohash (null si absent, hash inconnu ou liste illisible).</summary>
    private async Task<TorboxTorrent?> FindOnTorboxAsync(string? hash, CancellationToken ct)
    {
        hash = TorrentHash.Normalize(hash);
        if (hash is null)
        {
            return null;
        }

        try
        {
            var list = await _torbox.GetMyListAsync(ct).ConfigureAwait(false);
            return list.FirstOrDefault(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase));
        }
        catch (TorboxException ex)
        {
            _logger.LogWarning(ex, "Liste des torrents Torbox illisible : vérification des doublons impossible");
            return null;
        }
    }

    /// <summary>Envoie le torrent à Torbox. Doit être appelé sous <see cref="_lock"/>.</summary>
    private async Task SendToTorboxAsync(MediaRequest request, TorrentSource? source, CancellationToken ct)
    {
        var targets = SameTorrent(request, RequestStatus.Pending, RequestStatus.Approved, RequestStatus.AutoApproved, RequestStatus.Failed);
        try
        {
            // Torrent déjà présent sur le compte Torbox (ajouté à la main, hors plugin) : Torbox refuse les doublons,
            // on rattache donc la demande au torrent existant au lieu de le renvoyer.
            var hash = request.InfoHash;
            var existing = await FindOnTorboxAsync(hash, ct).ConfigureAwait(false);
            if (existing is null)
            {
                source ??= !string.IsNullOrEmpty(request.MagnetUrl)
                    ? new TorrentSource(null, request.MagnetUrl)
                    : !string.IsNullOrEmpty(request.DownloadUrl)
                        ? await _indexers.Current.DownloadAsync(request.DownloadUrl, ct).ConfigureAwait(false)
                        : throw new RequestException("Aucun lien de téléchargement pour ce torrent.");

                if (hash is null)
                {
                    hash = source.File is not null ? TorrentHash.FromTorrentFile(source.File) : TorrentHash.FromMagnet(source.Magnet);
                    existing = await FindOnTorboxAsync(hash, ct).ConfigureAwait(false);
                }
            }

            var adopted = existing is not null;
            if (existing is null)
            {
                try
                {
                    var created = await _torbox.CreateTorrentAsync(source!, request.TorrentTitle, ct).ConfigureAwait(false);
                    hash = created.Hash ?? hash;
                    existing = new TorboxTorrent(created.TorrentId ?? 0, hash, null, 0, null, false);
                }
                catch (TorboxException) when (hash is not null)
                {
                    // Refus de Torbox : si c'est un doublon, le torrent figure dans la liste du compte.
                    existing = await FindOnTorboxAsync(hash, ct).ConfigureAwait(false);
                    if (existing is null)
                    {
                        throw;
                    }

                    adopted = true;
                }
            }

            if (adopted)
            {
                _logger.LogInformation("Demande {Id} : torrent déjà présent sur Torbox (id {TorrentId}), rattaché", request.Id, existing.Id);
            }

            var finished = existing.DownloadFinished || existing.Progress >= 1;
            var message = adopted
                ? "est validée : le torrent était déjà sur Torbox, il est suivi par le plugin."
                : "est validée et envoyée à Torbox.";
            foreach (var r in targets)
            {
                r.Status = finished ? RequestStatus.CompletedOnTorbox : RequestStatus.SentToTorbox;
                r.TorboxTorrentId = existing.Id > 0 ? existing.Id : null;
                r.TorboxProgress = Math.Clamp(existing.Progress, 0, 1);
                r.InfoHash ??= existing.Hash ?? hash;
                r.LastError = null;
                if (r.Id != request.Id && r.ReviewedBy is null)
                {
                    r.ReviewedBy = request.ReviewedBy;
                    r.ReviewedAt = request.ReviewedAt;
                }

                _repository.Update(r);
                await _notifications.NotifyUserAsync(r.UserId, $"Votre demande « {Describe(r)} » {message}", r.Id, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Toute erreur d'envoi passe la demande « En échec » (relançable) : jamais d'erreur 500 côté utilisateur.
            _logger.LogError(ex, "Échec de l'envoi à Torbox de la demande {Id}", request.Id);
            foreach (var r in targets)
            {
                r.Status = RequestStatus.Failed;
                r.LastError = ex.Message;
                _repository.Update(r);
            }

            await _notifications.NotifyAdminsAsync($"Échec de l'envoi à Torbox de « {Describe(request)} » : {ex.Message}", request.Id, ct).ConfigureAwait(false);
            await _notifications.NotifyUserAsync(request.UserId, $"L'envoi de « {Describe(request)} » a échoué, un administrateur va le relancer.", request.Id, ct).ConfigureAwait(false);
        }
    }
}
