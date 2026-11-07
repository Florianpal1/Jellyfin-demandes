using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// <remarks>
/// Les transitions d'état se font sous <see cref="_lock"/>, sans aucun appel réseau : l'envoi à Torbox et les
/// notifications (webhook Discord) se font hors du verrou, pour qu'un Torbox lent ne bloque pas les autres demandes.
/// </remarks>
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

    /// <summary>Statuts d'une demande validée en attente de son envoi à Torbox (et demandes du même torrent qui suivent).</summary>
    private static readonly RequestStatus[] SendableStatuses =
    [
        RequestStatus.Pending,
        RequestStatus.Approved,
        RequestStatus.AutoApproved,
        RequestStatus.Failed,
    ];

    /// <summary>Statuts qu'un administrateur peut annuler (pas pendant un envoi à Torbox ni un téléchargement vers le NAS).</summary>
    private static readonly RequestStatus[] AdminCancellableStatuses =
    [
        RequestStatus.Pending,
        RequestStatus.Failed,
        .. SentStatuses,
    ];

    /// <summary>Statuts suivis pour la détection des demandes qui stagnent.</summary>
    private static readonly RequestStatus[] StaleWatchedStatuses =
    [
        RequestStatus.SentToTorbox,
        RequestStatus.DownloadingOnTorbox,
        RequestStatus.CompletedOnTorbox,
        RequestStatus.Imported,
    ];

    /// <summary>
    /// Délai avant de considérer qu'un torrent absent de la liste Torbox a vraiment disparu
    /// (laisse le temps à Torbox de lister un torrent tout juste ajouté).
    /// </summary>
    public static readonly TimeSpan MissingOnTorboxGracePeriod = TimeSpan.FromHours(1);

    /// <summary>Délai minimal entre deux actualisations manuelles (tous utilisateurs confondus) : ménage l'API Torbox.</summary>
    public static readonly TimeSpan ManualRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly RequestRepository _repository;
    private readonly SearchResultCache _cache;
    private readonly TorboxSubmitter _submitter;
    private readonly TorboxClient _torbox;
    private readonly ILibraryLookup _library;
    private readonly INotificationService _notifications;
    private readonly IConfigProvider _config;
    private readonly ILogger<RequestService> _logger;
    private readonly TimeProvider _time;

    // Sérialise les transitions d'état : évite par exemple un double envoi si deux admins valident en même temps.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly object _manualRefreshGate = new();
    private DateTime _lastManualRefresh = DateTime.MinValue;

    public RequestService(
        RequestRepository repository,
        SearchResultCache cache,
        TorboxSubmitter submitter,
        TorboxClient torbox,
        ILibraryLookup library,
        INotificationService notifications,
        IConfigProvider config,
        ILogger<RequestService> logger)
        : this(repository, cache, submitter, torbox, library, notifications, config, logger, TimeProvider.System)
    {
    }

    internal RequestService(
        RequestRepository repository,
        SearchResultCache cache,
        TorboxSubmitter submitter,
        TorboxClient torbox,
        ILibraryLookup library,
        INotificationService notifications,
        IConfigProvider config,
        ILogger<RequestService> logger,
        TimeProvider time)
    {
        _repository = repository;
        _cache = cache;
        _submitter = submitter;
        _torbox = torbox;
        _library = library;
        _notifications = notifications;
        _config = config;
        _logger = logger;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static string Describe(MediaRequest r) => r.Scope switch
    {
        RequestScope.Movie or RequestScope.Book => r.Year.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} ({r.Year})") : r.MediaTitle,
        RequestScope.Episode => string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} S{r.Season:00}E{r.Episode:00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{r.MediaTitle} saison {r.Season}"),
    };

    /// <summary>Libellé d'un statut, tel qu'affiché dans la page.</summary>
    public static string StatusLabel(RequestStatus status) => status switch
    {
        RequestStatus.Pending => "en attente de validation",
        RequestStatus.Approved => "validée",
        RequestStatus.AutoApproved => "auto-validée",
        RequestStatus.Rejected => "refusée",
        RequestStatus.SentToTorbox => "envoyée à Torbox",
        RequestStatus.DownloadingOnTorbox => "en téléchargement sur Torbox",
        RequestStatus.CompletedOnTorbox => "terminée sur Torbox",
        RequestStatus.Importing => "en téléchargement vers le NAS",
        RequestStatus.Imported => "rangée, en attente de Jellyfin",
        RequestStatus.ImportFailed => "rangement en échec",
        RequestStatus.Available => "disponible",
        RequestStatus.Failed => "en échec",
        RequestStatus.Cancelled => "annulée",
        _ => status.ToString(),
    };

    /// <summary>
    /// Actualisation demandée depuis l'interface (bouton « Actualiser ») : même travail que la tâche planifiée
    /// (progression Torbox puis arrivée dans la bibliothèque), au plus une fois toutes les 30 s.
    /// Renvoie false si l'actualisation a été ignorée (trop rapprochée) ; Torbox injoignable n'empêche pas la vérification de la bibliothèque.
    /// </summary>
    public async Task<bool> RefreshNowAsync(CancellationToken ct)
    {
        lock (_manualRefreshGate)
        {
            var now = Now;
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

    public async Task<MediaRequest> CreateAsync(Guid userId, string userName, string resultId, CancellationToken ct)
    {
        var entry = _cache.Get(resultId)
            ?? throw new RequestException("Ce résultat a expiré : relancez la recherche.", 410);
        var ctx = entry.Context;
        var res = entry.Result;

        var notices = new List<Notice>();
        MediaRequest request;
        bool send = false;
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
            request = new MediaRequest
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

            // Même torrent déjà demandé par quelqu'un d'autre : on suit la demande existante (son envoi éventuel
            // en cours mettra aussi celle-ci à jour).
            var sibling = siblings.FirstOrDefault();
            if (sibling is not null)
            {
                request.Status = sibling.Status;
                request.AutoApprovalReason = sibling.AutoApprovalReason;
                request.TorboxTorrentId = sibling.TorboxTorrentId;
                request.TorboxProgress = sibling.TorboxProgress;
                _repository.Insert(request);
                notices.Add(Notice.User(request, $"« {Describe(request)} » a déjà été demandé : votre demande suit la demande existante."));
            }
            else if (reason == AutoApprovalReason.None)
            {
                request.Status = RequestStatus.Pending;
                _repository.Insert(request);
                notices.Add(Notice.Admins(request, $"Nouvelle demande à valider : « {Describe(request)} » par {userName} ({FormatSize(request.SizeBytes)}, {request.Seeders} seeders)."));
            }
            else
            {
                request.Status = RequestStatus.AutoApproved;
                _repository.Insert(request);
                send = true;
                var why = reason == AutoApprovalReason.Cached ? "déjà en cache Torbox" : "sous le seuil de taille";
                notices.Add(Notice.Admins(request, $"Demande auto-validée ({why}) : « {Describe(request)} » par {userName}."));
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
        if (!send)
        {
            return request;
        }

        // Pas d'annulation : une déconnexion du navigateur ne doit pas laisser la demande à moitié envoyée.
        await SendToTorboxAsync(request, entry.Source, CancellationToken.None).ConfigureAwait(false);
        return _repository.Get(request.Id) ?? request;
    }

    public async Task<MediaRequest> ApproveAsync(long id, string adminName, CancellationToken ct)
    {
        MediaRequest request;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (request.Status is not (RequestStatus.Pending or RequestStatus.Failed))
            {
                throw new RequestException("Seule une demande en attente ou en échec peut être validée.", 409);
            }

            // « Approved » réserve la demande : un second admin reçoit une erreur 409 pendant l'envoi.
            request.Status = RequestStatus.Approved;
            request.ReviewedBy = adminName;
            request.ReviewedAt = Now;
            request.LastError = null;
            _repository.Update(request);
        }
        finally
        {
            _lock.Release();
        }

        await SendToTorboxAsync(request, null, CancellationToken.None).ConfigureAwait(false);
        return _repository.Get(id) ?? request;
    }

    public async Task<MediaRequest> RejectAsync(long id, string adminName, string? reason, CancellationToken ct)
    {
        var notices = new List<Notice>();
        MediaRequest request;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (request.Status is not (RequestStatus.Pending or RequestStatus.Failed))
            {
                throw new RequestException("Seule une demande en attente ou en échec peut être refusée.", 409);
            }

            var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            foreach (var r in SameTorrent(request, RequestStatus.Pending, RequestStatus.Failed))
            {
                r.Status = RequestStatus.Rejected;
                r.ReviewedBy = adminName;
                r.ReviewedAt = Now;
                r.RejectReason = cleanReason;
                _repository.Update(r);
                notices.Add(Notice.User(r, $"Votre demande « {Describe(r)} » a été refusée" + (cleanReason is null ? "." : $" : {cleanReason}")));
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
        return request;
    }

    /// <summary>
    /// Annule une demande. Un utilisateur annule sa propre demande tant qu'elle n'est pas partie sur Torbox
    /// (<paramref name="userId"/> renseigné) ; un administrateur annule toute demande en cours, sauf pendant
    /// un envoi à Torbox ou un téléchargement vers le NAS, et peut supprimer le torrent de Torbox.
    /// </summary>
    public async Task<MediaRequest> CancelAsync(long id, Guid? userId, string actorName, bool deleteFromTorbox, CancellationToken ct)
    {
        var notices = new List<Notice>();
        MediaRequest request;
        long? torrentToDelete = null;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            request = _repository.Get(id) is { } found && (!userId.HasValue || found.UserId == userId.Value)
                ? found
                : throw new RequestException("Demande introuvable.", 404);

            if (userId.HasValue && request.Status is not (RequestStatus.Pending or RequestStatus.Failed))
            {
                throw new RequestException("Seule une demande en attente ou en échec peut être annulée : elle est déjà partie sur Torbox.", 409);
            }

            if (!AdminCancellableStatuses.Contains(request.Status))
            {
                throw new RequestException(request.Status == RequestStatus.Importing
                    ? "Téléchargement vers le NAS en cours : attendez la fin avant d'annuler."
                    : "Cette demande ne peut plus être annulée.", 409);
            }

            request.Status = RequestStatus.Cancelled;
            request.LastError = null;
            request.RetryImportAfter = null;
            if (!userId.HasValue)
            {
                request.ReviewedBy = actorName;
                request.ReviewedAt = Now;
            }

            _repository.Update(request);

            // Le torrent n'est supprimé que si aucune autre demande en cours n'en a besoin.
            if (deleteFromTorbox && !userId.HasValue && request.TorboxTorrentId is { } torrentId
                && !_repository.GetActive().Any(r => r.TorboxTorrentId == torrentId || SameHash(r, request)))
            {
                torrentToDelete = torrentId;
            }

            if (!userId.HasValue && request.UserId != Guid.Empty)
            {
                notices.Add(Notice.User(request, $"Votre demande « {Describe(request)} » a été annulée par un administrateur."));
            }
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Demande {Id} annulée par {Actor}", request.Id, actorName);
        if (torrentToDelete is { } tid)
        {
            try
            {
                await _torbox.DeleteTorrentAsync(tid, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TorboxException ex)
            {
                _logger.LogWarning(ex, "Suppression du torrent {TorrentId} sur Torbox impossible", tid);
                await UpdateAsync(request.Id, r => r.LastError = "Suppression sur Torbox impossible : " + ex.Message).ConfigureAwait(false);
            }
        }

        await NotifyAsync(notices).ConfigureAwait(false);
        return _repository.Get(id) ?? request;
    }

    /// <summary>
    /// Passe une demande (et celles du même torrent) en « Disponible » à la main : média arrivé dans Jellyfin
    /// sans être reconnu (titre ou identifiants différents).
    /// </summary>
    public async Task<MediaRequest> MarkAvailableAsync(long id, string adminName, CancellationToken ct)
    {
        var notices = new List<Notice>();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = _repository.Get(id) ?? throw new RequestException("Demande introuvable.", 404);
            if (!SentStatuses.Contains(request.Status))
            {
                throw new RequestException("Seule une demande envoyée à Torbox peut être marquée disponible.", 409);
            }

            foreach (var r in SameTorrent(request, SentStatuses))
            {
                r.Status = RequestStatus.Available;
                r.TorboxProgress = 1;
                r.LastError = null;
                r.RetryImportAfter = null;
                r.ReviewedBy ??= adminName;
                _repository.Update(r);
                notices.Add(Notice.User(r, $"« {Describe(r)} » est disponible dans Jellyfin."));
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
        return _repository.Get(id)!;
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
                r.ImportAttempts = 0;
                r.RetryImportAfter = null;
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

    /// <summary>
    /// Au démarrage du serveur : un import interrompu repart (le fichier partiel est repris) ; un envoi à Torbox
    /// interrompu passe « En échec » pour qu'un admin le renvoie (aucun envoi ne peut être en cours à ce moment).
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await UpdateActiveAsync(r => r.Status == RequestStatus.Importing, r => r.Status = RequestStatus.CompletedOnTorbox, ct).ConfigureAwait(false);
        var interrupted = await UpdateActiveAsync(
            r => r.Status is RequestStatus.Approved or RequestStatus.AutoApproved,
            r =>
            {
                r.Status = RequestStatus.Failed;
                r.LastError = "Envoi à Torbox interrompu par un arrêt du serveur : renvoyez la demande.";
            },
            ct).ConfigureAwait(false);
        await NotifyAsync(interrupted.Select(r => Notice.Admins(r, $"Envoi à Torbox de « {Describe(r)} » interrompu par un arrêt du serveur : renvoyez la demande."))).ConfigureAwait(false);
    }

    /// <summary>
    /// Met à jour la progression Torbox des demandes envoyées. Un torrent en erreur sur Torbox, ou absent du compte
    /// pendant plus de <see cref="MissingOnTorboxGracePeriod"/>, passe la demande « En échec » (renvoi par un admin).
    /// </summary>
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
        var notices = new List<Notice>();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Now;
            foreach (var r in active)
            {
                var current = _repository.Get(r.Id);
                if (current is null || current.Status is not (RequestStatus.SentToTorbox or RequestStatus.DownloadingOnTorbox))
                {
                    continue;
                }

                var t = torrents.FirstOrDefault(t => current.TorboxTorrentId.HasValue && t.Id == current.TorboxTorrentId.Value)
                    ?? torrents.FirstOrDefault(t => current.InfoHash is not null && string.Equals(t.Hash, current.InfoHash, StringComparison.OrdinalIgnoreCase));
                if (t is null)
                {
                    if (!current.TorboxTorrentId.HasValue && current.InfoHash is null)
                    {
                        continue;
                    }

                    current.MissingOnTorboxSince ??= now;
                    if (now - current.MissingOnTorboxSince.Value >= MissingOnTorboxGracePeriod)
                    {
                        MarkTorboxFailed(current, "Le torrent n'est plus sur le compte Torbox (supprimé ou expiré) : renvoyez la demande.", notices);
                    }

                    _repository.Update(current);
                    continue;
                }

                current.MissingOnTorboxSince = null;
                current.TorboxTorrentId ??= t.Id;
                if (TorboxStates.IsError(t.DownloadState))
                {
                    MarkTorboxFailed(current, $"Torbox signale une erreur ({t.DownloadState}) : renvoyez la demande pour relancer le torrent.", notices);
                    _repository.Update(current);
                    continue;
                }

                current.TorboxProgress = Math.Clamp(t.Progress, 0, 1);
                current.Status = t.DownloadFinished || t.Progress >= 1
                    ? RequestStatus.CompletedOnTorbox
                    : RequestStatus.DownloadingOnTorbox;

                // Torrent sans source : affiché sous le statut, la demande reste en cours.
                current.LastError = TorboxStates.IsStalled(t.DownloadState) && current.Status == RequestStatus.DownloadingOnTorbox
                    ? $"Torbox : {t.DownloadState}"
                    : null;
                _repository.Update(current);
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
    }

    /// <summary>Passe en « Disponible » les demandes dont le média est arrivé dans la bibliothèque.</summary>
    public async Task CheckAvailabilityAsync(CancellationToken ct)
    {
        var notices = new List<Notice>();
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
                r.LastError = null;
                r.RetryImportAfter = null;
                _repository.Update(r);
                notices.Add(Notice.User(r, $"« {Describe(r)} » est disponible dans Jellyfin."));
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
    }

    /// <summary>
    /// Prévient les admins (une seule fois par demande) des demandes toujours en cours après
    /// <see cref="Configuration.PluginConfiguration.StaleRequestDays"/> jours : torrent sans source, média non reconnu par Jellyfin…
    /// Renvoie les demandes signalées.
    /// </summary>
    public async Task<IReadOnlyList<MediaRequest>> CheckStaleAsync(CancellationToken ct)
    {
        var days = _config.Current.StaleRequestDays;
        if (days <= 0)
        {
            return Array.Empty<MediaRequest>();
        }

        var now = Now;
        var limit = now.AddDays(-days);
        var stale = await UpdateActiveAsync(
            r => StaleWatchedStatuses.Contains(r.Status) && r.StaleNotifiedAt is null && (r.ReviewedAt ?? r.CreatedAt) < limit,
            r => r.StaleNotifiedAt = now,
            ct).ConfigureAwait(false);
        await NotifyAsync(stale.Select(r => Notice.Admins(
            r,
            $"« {Describe(r)} » ({r.UserName}) est toujours « {StatusLabel(r.Status)} » après {days} jours : vérifiez-la, puis marquez-la disponible ou annulez-la."))).ConfigureAwait(false);
        return stale;
    }

    /// <summary>Supprime les demandes terminées et les notifications lues trop anciennes (durées réglées dans la configuration).</summary>
    public (int Requests, int Notifications) Purge()
    {
        var cfg = _config.Current;
        var now = Now;
        var result = _repository.Purge(
            cfg.RequestRetentionDays > 0 ? now.AddDays(-cfg.RequestRetentionDays) : DateTime.MinValue,
            cfg.NotificationRetentionDays > 0 ? now.AddDays(-cfg.NotificationRetentionDays) : DateTime.MinValue);
        if (result.Requests > 0 || result.Notifications > 0)
        {
            _logger.LogInformation("Ménage : {Requests} demande(s) et {Notifications} notification(s) supprimées", result.Requests, result.Notifications);
        }

        return result;
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

    private static bool SameHash(MediaRequest a, MediaRequest b)
        => a.Id != b.Id && a.InfoHash is not null && string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase);

    private static void MarkTorboxFailed(MediaRequest r, string error, List<Notice> notices)
    {
        r.Status = RequestStatus.Failed;
        r.LastError = error;
        r.MissingOnTorboxSince = null;
        notices.Add(Notice.Admins(r, $"« {Describe(r)} » : {error}"));
        notices.Add(Notice.User(r, $"Le téléchargement de « {Describe(r)} » a échoué sur Torbox, un administrateur va le relancer."));
    }

    /// <summary>La demande et les autres demandes du même torrent dans l'un des statuts donnés.</summary>
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
                    .Where(r => SameHash(r, request)));
            }
        }

        return list.DistinctBy(r => r.Id).ToList();
    }

    private async Task UpdateAsync(long id, Action<MediaRequest> apply)
    {
        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_repository.Get(id) is { } r)
            {
                apply(r);
                _repository.Update(r);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Envoie le torrent à Torbox (hors verrou), puis enregistre le résultat sur la demande et les demandes
    /// du même torrent. Toute erreur passe la demande « En échec » (relançable) : jamais d'erreur 500 côté utilisateur.
    /// </summary>
    private async Task SendToTorboxAsync(MediaRequest request, TorrentSource? source, CancellationToken ct)
    {
        TorboxSubmission? submission = null;
        Exception? error = null;
        try
        {
            submission = await _submitter.SubmitAsync(request, source, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Échec de l'envoi à Torbox de la demande {Id}", request.Id);
            error = ex;
        }

        var notices = new List<Notice>();
        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = _repository.Get(request.Id);
            if (current is null)
            {
                return;
            }

            // Relu sous le verrou : inclut les demandes du même torrent arrivées pendant l'envoi.
            var targets = SameTorrent(current, SendableStatuses).Where(r => SendableStatuses.Contains(r.Status)).ToList();
            if (submission is null)
            {
                var message = error?.Message ?? "Envoi à Torbox impossible.";
                foreach (var r in targets)
                {
                    r.Status = RequestStatus.Failed;
                    r.LastError = message;
                    _repository.Update(r);
                }

                notices.Add(Notice.Admins(current, $"Échec de l'envoi à Torbox de « {Describe(current)} » : {message}"));
                notices.Add(Notice.User(current, $"L'envoi de « {Describe(current)} » a échoué, un administrateur va le relancer."));
            }
            else
            {
                var torrent = submission.Torrent;
                var text = submission.Adopted
                    ? "est validée : le torrent était déjà sur Torbox, il est suivi par le plugin."
                    : "est validée et envoyée à Torbox.";
                foreach (var r in targets)
                {
                    r.Status = submission.Finished ? RequestStatus.CompletedOnTorbox : RequestStatus.SentToTorbox;
                    r.TorboxTorrentId = torrent.Id > 0 ? torrent.Id : null;
                    r.TorboxProgress = Math.Clamp(torrent.Progress, 0, 1);
                    r.InfoHash ??= submission.Hash;
                    r.LastError = null;
                    r.MissingOnTorboxSince = null;
                    if (r.Id != current.Id && r.ReviewedBy is null)
                    {
                        r.ReviewedBy = current.ReviewedBy;
                        r.ReviewedAt = current.ReviewedAt;
                    }

                    _repository.Update(r);
                    notices.Add(Notice.User(r, $"Votre demande « {Describe(r)} » {text}"));
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        await NotifyAsync(notices).ConfigureAwait(false);
    }

    /// <summary>Envoie les notifications préparées sous le verrou (une notification ratée ne fait jamais échouer une demande).</summary>
    private async Task NotifyAsync(IEnumerable<Notice> notices)
    {
        foreach (var n in notices)
        {
            try
            {
                if (n.UserId is { } userId)
                {
                    await _notifications.NotifyUserAsync(userId, n.Message, n.RequestId, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _notifications.NotifyAdminsAsync(n.Message, n.RequestId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification de la demande {Id} impossible", n.RequestId);
            }
        }
    }

    /// <summary>Notification préparée sous le verrou, envoyée après (destinataire null = administrateurs).</summary>
    private sealed record Notice(Guid? UserId, string Message, long RequestId)
    {
        public static Notice User(MediaRequest r, string message) => new(r.UserId, message, r.Id);

        public static Notice Admins(MediaRequest r, string message) => new(null, message, r.Id);
    }
}
