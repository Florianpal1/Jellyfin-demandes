using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Data;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>Signale à Jellyfin qu'un dossier de la bibliothèque a changé (scan ciblé).</summary>
public interface ILibraryRefresher
{
    void Refresh(string path);
}

/// <summary>
/// Remplace l'application « RealDebrid » + Radarr / Sonarr : télécharge depuis Torbox les fichiers des
/// demandes terminées, puis les range dans la bibliothèque avec le nommage standard Radarr / Sonarr.
/// Une seule demande (un seul torrent) est traitée à la fois.
/// </summary>
public class ImportService
{
    private const int AttemptsPerFile = 3;

    /// <summary>Marge d'espace disque gardée libre en plus des fichiers à télécharger.</summary>
    private const long FreeSpaceMargin = 1L * 1024 * 1024 * 1024;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly RequestRepository _repository;
    private readonly RequestService _requests;
    private readonly TorboxClient _torbox;
    private readonly FileDownloader _downloader;
    private readonly ILibraryLookup _library;
    private readonly IEpisodeTitleProvider _titles;
    private readonly ILibraryRefresher _refresher;
    private readonly INotificationService _notifications;
    private readonly IConfigProvider _config;
    private readonly ILogger<ImportService> _logger;

    public ImportService(
        RequestRepository repository,
        RequestService requests,
        TorboxClient torbox,
        FileDownloader downloader,
        ILibraryLookup library,
        IEpisodeTitleProvider titles,
        ILibraryRefresher refresher,
        INotificationService notifications,
        IConfigProvider config,
        ILogger<ImportService> logger)
    {
        _repository = repository;
        _requests = requests;
        _torbox = torbox;
        _downloader = downloader;
        _library = library;
        _titles = titles;
        _refresher = refresher;
        _notifications = notifications;
        _config = config;
        _logger = logger;
    }

    public bool Enabled => _config.Current.AutoImportEnabled;

    /// <summary>Dossier racine des films ou des séries : réglage du plugin, sinon première bibliothèque Jellyfin du bon type.</summary>
    public string? ResolveRoot(MediaKind kind)
    {
        var configured = (kind == MediaKind.Movie ? _config.Current.MoviesPath : _config.Current.SeriesPath)?.Trim();
        return string.IsNullOrEmpty(configured) ? _library.GetLibraryFolders(kind).FirstOrDefault() : configured;
    }

    /// <summary>Importe la plus ancienne demande terminée sur Torbox. Renvoie false s'il n'y avait rien à faire.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return false;
        }

        var next = _repository.GetActive()
            .Where(r => r.Status == RequestStatus.CompletedOnTorbox)
            .OrderBy(r => r.UpdatedAt)
            .FirstOrDefault();
        if (next is null)
        {
            return false;
        }

        // Le média est peut-être déjà arrivé autrement (ancienne application de récupération) : rien à télécharger.
        await _requests.CheckAvailabilityAsync(ct).ConfigureAwait(false);
        next = _repository.Get(next.Id);
        if (next is null || next.Status != RequestStatus.CompletedOnTorbox)
        {
            return true;
        }

        await ImportAsync(next, ct).ConfigureAwait(false);
        return true;
    }

    private static bool SameTorrent(MediaRequest a, MediaRequest b)
        => (a.TorboxTorrentId.HasValue && a.TorboxTorrentId == b.TorboxTorrentId)
            || (a.InfoHash is not null && string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase));

    private async Task ImportAsync(MediaRequest first, CancellationToken ct)
    {
        var group = await _requests.UpdateActiveAsync(
            r => r.Status == RequestStatus.CompletedOnTorbox && SameTorrent(r, first),
            r =>
            {
                r.Status = RequestStatus.Importing;
                r.ImportProgress = 0;
                r.LastError = null;
            },
            ct).ConfigureAwait(false);
        if (group.Count == 0)
        {
            return;
        }

        // La demande la plus large guide le choix des fichiers (pack de saison avant épisode seul).
        var primary = group.OrderBy(r => r.Scope switch { RequestScope.Season => 0, RequestScope.Episode => 1, _ => 2 }).First();
        bool IsMine(MediaRequest r) => r.Status == RequestStatus.Importing && SameTorrent(r, primary);

        try
        {
            var (folder, plan, torrentId) = await PrepareAsync(primary, ct).ConfigureAwait(false);
            await DownloadAllAsync(primary, plan, torrentId, IsMine, ct).ConfigureAwait(false);

            await _requests.UpdateActiveAsync(IsMine, r =>
            {
                r.Status = RequestStatus.Imported;
                r.ImportProgress = 1;
                r.ImportedPath = folder;
            }, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "« {Title} » rangé dans {Folder} ({Count} fichier(s)). Ignorés : {Skipped}",
                RequestService.Describe(primary),
                folder,
                plan.Items.Count,
                plan.Skipped.Count == 0 ? "aucun" : string.Join(" ; ", plan.Skipped));

            foreach (var dir in plan.Items.Select(i => Path.GetDirectoryName(i.Destination)!).Distinct(StringComparer.Ordinal))
            {
                _refresher.Refresh(dir);
            }

            if (_config.Current.DeleteFromTorboxAfterImport)
            {
                await DeleteFromTorboxAsync(torrentId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt du serveur : le fichier partiel est gardé, l'import reprendra au prochain démarrage.
            await _requests.UpdateActiveAsync(IsMine, r => r.Status = RequestStatus.CompletedOnTorbox, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Toute autre erreur (disque, réseau, réponse inattendue) : la demande ne doit pas rester bloquée en « Importing ».
            _logger.LogError(ex, "Import de « {Title} » en échec", RequestService.Describe(primary));
            await _requests.UpdateActiveAsync(IsMine, r =>
            {
                r.Status = RequestStatus.ImportFailed;
                r.LastError = ex.Message;
            }, CancellationToken.None).ConfigureAwait(false);
            await _notifications.NotifyAdminsAsync(
                $"Échec du rangement de « {RequestService.Describe(primary)} » : {ex.Message}", primary.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Lit les fichiers sur Torbox, résout les dossiers et calcule le plan d'import.</summary>
    private async Task<(string Folder, ImportPlan Plan, long TorrentId)> PrepareAsync(MediaRequest request, CancellationToken ct)
    {
        TorboxTorrent? torrent = null;
        if (request.TorboxTorrentId is { } id)
        {
            torrent = await _torbox.GetTorrentAsync(id, ct).ConfigureAwait(false);
        }

        if (torrent is null && request.InfoHash is not null)
        {
            var all = await _torbox.GetMyListAsync(ct).ConfigureAwait(false);
            var found = all.FirstOrDefault(t => string.Equals(t.Hash, request.InfoHash, StringComparison.OrdinalIgnoreCase));
            torrent = found is null ? null : await _torbox.GetTorrentAsync(found.Id, ct).ConfigureAwait(false) ?? found;
        }

        if (torrent is null)
        {
            throw new ImportException("Le torrent n'existe plus sur Torbox.");
        }

        if (torrent.Files.Count == 0)
        {
            throw new ImportException("Torbox ne liste aucun fichier pour ce torrent.");
        }

        var root = ResolveRoot(request.MediaKind);
        if (string.IsNullOrEmpty(root))
        {
            throw new ImportException(request.MediaKind == MediaKind.Movie
                ? "Aucun dossier de films : renseignez-le dans la configuration du plugin."
                : "Aucun dossier de séries : renseignez-le dans la configuration du plugin.");
        }

        if (!Directory.Exists(root))
        {
            throw new ImportException($"Le dossier « {root} » est introuvable depuis le serveur Jellyfin.");
        }

        var ids = RequestService.ProviderIdsOf(request);
        ImportTarget target;
        if (request.MediaKind == MediaKind.Movie)
        {
            target = new ImportTarget
            {
                Folder = Path.Combine(root, LibraryNaming.MovieFolder(request.MediaTitle, request.Year, request.TmdbId)),
                IncludeSubtitles = _config.Current.ImportSubtitles,
            };
        }
        else
        {
            // Série déjà présente : on range dans son dossier actuel, comme Sonarr.
            var seriesFolder = _library.GetSeriesFolder(ids)
                ?? Path.Combine(root, LibraryNaming.SeriesFolder(request.MediaTitle, request.Year, request.TmdbId));
            var episodes = ImportPlanner.FindEpisodes(request, torrent.Files);
            var titles = await _titles.GetTitlesAsync(ids, episodes, ct).ConfigureAwait(false);
            target = new ImportTarget
            {
                Folder = seriesFolder,
                SeasonFolder = s => _library.GetSeasonFolder(ids, s) ?? Path.Combine(seriesFolder, LibraryNaming.SeasonFolder(s)),
                EpisodeTitles = titles,
                EpisodeExists = (s, e) => _library.CountEpisodes(ids, s, e) > 0,
                IncludeSubtitles = _config.Current.ImportSubtitles,
            };
        }

        var plan = ImportPlanner.Plan(request, torrent.Files, target);
        EnsureInside(target.Folder, plan);
        EnsureNoOverwrite(plan);
        EnsureFreeSpace(target.Folder, root, plan);
        return (target.Folder, plan, torrent.Id);
    }

    /// <summary>Garde-fou : aucun fichier ne doit sortir du dossier du média (noms déjà nettoyés, mais on vérifie).</summary>
    private static void EnsureInside(string folder, ImportPlan plan)
    {
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var item in plan.Items)
        {
            if (!Path.GetFullPath(item.Destination).StartsWith(baseDir, StringComparison.Ordinal))
            {
                throw new ImportException($"Chemin de destination refusé : {item.Destination}");
            }
        }
    }

    /// <summary>
    /// Un fichier différent existe déjà à la destination : on ne l'écrase jamais (un fichier identique,
    /// de même taille, est considéré comme déjà rangé).
    /// </summary>
    private static void EnsureNoOverwrite(ImportPlan plan)
    {
        foreach (var item in plan.Items)
        {
            if (File.Exists(item.Destination) && new FileInfo(item.Destination).Length != item.File.Size)
            {
                throw new ImportException($"Un autre fichier existe déjà à « {item.Destination} » : il n'est pas écrasé. Déplacez-le puis relancez.");
            }
        }
    }

    private void EnsureFreeSpace(string folder, string root, ImportPlan plan)
    {
        long needed = plan.Items.Sum(i => Math.Max(0, i.File.Size - AlreadyOnDisk(i)));
        long? free = null;
        try
        {
            free = new DriveInfo(Directory.Exists(folder) ? folder : root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Espace libre de {Folder} inconnu", root);
        }

        if (free is { } f && f < needed + FreeSpaceMargin)
        {
            throw new ImportException(
                $"Espace disque insuffisant dans « {root} » : {RequestService.FormatSize(needed)} nécessaires, {RequestService.FormatSize(f)} libres.");
        }
    }

    /// <summary>Octets déjà présents : fichier final complet, ou fichier partiel d'un import interrompu.</summary>
    private static long AlreadyOnDisk(ImportItem item)
    {
        if (File.Exists(item.Destination) && new FileInfo(item.Destination).Length == item.File.Size)
        {
            return item.File.Size;
        }

        var part = item.Destination + FileDownloader.PartialExtension;
        return File.Exists(part) ? Math.Min(new FileInfo(part).Length, item.File.Size) : 0;
    }

    private async Task DownloadAllAsync(MediaRequest primary, ImportPlan plan, long torrentId, Func<MediaRequest, bool> isMine, CancellationToken ct)
    {
        long total = Math.Max(1, plan.TotalBytes);
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        foreach (var item in plan.Items)
        {
            if (File.Exists(item.Destination) && item.File.Size > 0 && new FileInfo(item.Destination).Length == item.File.Size)
            {
                done += item.File.Size;
                continue;
            }

            long current = 0;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var url = await _torbox.RequestDownloadUrlAsync(torrentId, item.File.Id, ct).ConfigureAwait(false);
                    await _downloader.DownloadAsync(
                        url,
                        item.Destination,
                        item.File.Size,
                        bytes =>
                        {
                            current = bytes;
                            if (clock.Elapsed - lastReport < ProgressInterval)
                            {
                                return;
                            }

                            lastReport = clock.Elapsed;
                            var progress = Math.Clamp((done + bytes) / (double)total, 0, 0.99);
                            _ = ReportProgressAsync(isMine, progress);
                        },
                        ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < AttemptsPerFile && !ct.IsCancellationRequested
                    && ex is ImportException or TorboxException or HttpRequestException or IOException)
                {
                    // Coupure réseau ou lien expiré : nouveau lien, reprise là où le fichier partiel s'est arrêté.
                    _logger.LogWarning(ex, "Téléchargement de {File} interrompu (essai {Attempt}/{Max}), nouvelle tentative", item.File.Name, attempt, AttemptsPerFile);
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct).ConfigureAwait(false);
                }
            }

            done += item.File.Size > 0 ? item.File.Size : current;
        }

        _logger.LogDebug("Import de {Title} : {Bytes} octets en {Elapsed}", RequestService.Describe(primary), done, clock.Elapsed);
    }

    /// <summary>Progression affichée dans la page (au plus toutes les 5 s) ; une erreur ici ne doit pas interrompre le téléchargement.</summary>
    private async Task ReportProgressAsync(Func<MediaRequest, bool> isMine, double progress)
    {
        try
        {
            await _requests.UpdateActiveAsync(isMine, r => r.ImportProgress = progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mise à jour de la progression d'import impossible");
        }
    }

    private async Task DeleteFromTorboxAsync(long torrentId)
    {
        // D'autres demandes actives sur ce torrent (autre saison, par exemple) en ont encore besoin.
        if (_repository.GetActive().Any(r => r.TorboxTorrentId == torrentId && r.Status is not (RequestStatus.Imported or RequestStatus.Importing)))
        {
            return;
        }

        try
        {
            await _torbox.DeleteTorrentAsync(torrentId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TorboxException ex)
        {
            _logger.LogWarning(ex, "Suppression du torrent {Id} sur Torbox impossible", torrentId);
        }
    }
}
