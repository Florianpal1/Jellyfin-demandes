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
/// demandes terminées, puis les range dans la bibliothèque avec le nommage standard Radarr / Sonarr
/// (livres : un dossier « Titre (Année) » par demande).
/// Plusieurs torrents, et plusieurs fichiers d'un même torrent, peuvent être téléchargés en parallèle :
/// le nombre total de téléchargements simultanés est borné par <see cref="ParallelDownloads"/>.
/// </summary>
public class ImportService
{
    private const int AttemptsPerFile = 3;

    /// <summary>Tentatives de rangement automatiques avant l'échec définitif (« Relancer » par un admin).</summary>
    internal const int MaxImportAttempts = 3;

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
    private readonly DownloadGate _gate;

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
        _gate = new DownloadGate(() => ParallelDownloads);
    }

    public bool Enabled => _config.Current.AutoImportEnabled;

    /// <summary>Nombre max de fichiers téléchargés en même temps, tous imports confondus (1 à 10).</summary>
    public int ParallelDownloads => Math.Clamp(_config.Current.MaxParallelDownloads, 1, 10);

    /// <summary>Dossier racine des films, séries ou livres : réglage du plugin, sinon première bibliothèque Jellyfin du bon type.</summary>
    public string? ResolveRoot(MediaKind kind)
    {
        var configured = (kind switch
        {
            MediaKind.Movie => _config.Current.MoviesPath,
            MediaKind.Book => _config.Current.BooksPath,
            _ => _config.Current.SeriesPath,
        })?.Trim();
        return string.IsNullOrEmpty(configured) ? _library.GetLibraryFolders(kind).FirstOrDefault() : configured;
    }

    /// <summary>Importe la plus ancienne demande terminée sur Torbox. Renvoie false s'il n'y avait rien à faire.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var import = await TryStartNextAsync(ct).ConfigureAwait(false);
        if (import is null)
        {
            return false;
        }

        await import.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Réserve la plus ancienne demande terminée sur Torbox (passée en « Importing ») et lance son import
    /// en arrière-plan. Renvoie la tâche de l'import, ou null s'il n'y avait rien à faire.
    /// </summary>
    public async Task<Task?> TryStartNextAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var next = _repository.GetActive()
            .Where(r => r.Status == RequestStatus.CompletedOnTorbox && (r.RetryImportAfter is null || r.RetryImportAfter <= now))
            .OrderBy(r => r.UpdatedAt)
            .FirstOrDefault();
        if (next is null)
        {
            return null;
        }

        // Le média est peut-être déjà arrivé autrement (ancienne application de récupération) : rien à télécharger.
        await _requests.CheckAvailabilityAsync(ct).ConfigureAwait(false);
        next = _repository.Get(next.Id);
        if (next is null || next.Status != RequestStatus.CompletedOnTorbox)
        {
            return Task.CompletedTask;
        }

        var group = await ClaimAsync(next, ct).ConfigureAwait(false);
        return group.Count == 0 ? Task.CompletedTask : Task.Run(() => ImportAsync(group, ct), CancellationToken.None);
    }

    private static bool SameTorrent(MediaRequest a, MediaRequest b)
        => (a.TorboxTorrentId.HasValue && a.TorboxTorrentId == b.TorboxTorrentId)
            || (a.InfoHash is not null && string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase));

    /// <summary>Passe en « Importing » les demandes de ce torrent (sous verrou : un même torrent n'est jamais importé deux fois).</summary>
    private Task<IReadOnlyList<MediaRequest>> ClaimAsync(MediaRequest first, CancellationToken ct)
        => _requests.UpdateActiveAsync(
            r => r.Status == RequestStatus.CompletedOnTorbox && SameTorrent(r, first),
            r =>
            {
                r.Status = RequestStatus.Importing;
                r.ImportProgress = 0;
                r.LastError = null;
                r.RetryImportAfter = null;
            },
            ct);

    private async Task ImportAsync(IReadOnlyList<MediaRequest> group, CancellationToken ct)
    {
        // La demande la plus large guide le choix des fichiers (pack de saison avant épisode seul).
        var primary = group.OrderBy(r => r.Scope switch { RequestScope.Season => 0, RequestScope.Episode => 1, _ => 2 }).First();
        bool IsMine(MediaRequest r) => r.Status == RequestStatus.Importing && SameTorrent(r, primary);

        // Remplacement d'un média de la bibliothèque : anciens fichiers, supprimés une fois les nouveaux rangés.
        var replaced = group.SelectMany(RequestService.ReplacedFilesOf).DistinctBy(f => f.Path, PathComparer).ToList();
        var replacedPaths = new HashSet<string>(replaced.Select(f => Path.GetFullPath(f.Path)), PathComparer);

        try
        {
            var (folder, plan, torrentId, episodes) = await PrepareAsync(primary, replaced.Count > 0, replacedPaths, ct).ConfigureAwait(false);
            await DownloadAllAsync(primary, plan, torrentId, IsMine, replacedPaths, ct).ConfigureAwait(false);

            var oldFolders = new List<string>();
            if (replaced.Count > 0)
            {
                var (folders, problems) = DeleteReplacedFiles(replaced, plan, episodes);
                oldFolders = folders;
                if (problems.Count > 0)
                {
                    await _notifications.NotifyAdminsAsync(
                        $"Remplacement de « {RequestService.Describe(primary)} » : nouveau fichier rangé, mais {string.Join(" ; ", problems)}", primary.Id, CancellationToken.None).ConfigureAwait(false);
                }
            }

            await _requests.UpdateActiveAsync(IsMine, r =>
            {
                r.Status = RequestStatus.Imported;
                r.ImportProgress = 1;
                r.ImportedPath = folder;
                r.ImportAttempts = 0;
            }, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "« {Title} » rangé dans {Folder} ({Count} fichier(s)). Ignorés : {Skipped}",
                RequestService.Describe(primary),
                folder,
                plan.Items.Count,
                plan.Skipped.Count == 0 ? "aucun" : string.Join(" ; ", plan.Skipped));

            foreach (var dir in plan.Items.Select(i => Path.GetDirectoryName(i.Destination)!).Concat(oldFolders).Distinct(StringComparer.Ordinal))
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
            // Toute autre erreur (disque, réseau, NAS démonté, réponse inattendue) : la demande ne doit pas rester bloquée
            // en « Importing ». Nouvel essai automatique plus tard, puis échec définitif signalé aux admins.
            var attempt = primary.ImportAttempts + 1;
            if (attempt < MaxImportAttempts)
            {
                var retryAt = DateTime.UtcNow + RetryDelay(attempt);
                _logger.LogWarning(ex, "Import de « {Title} » en échec (essai {Attempt}/{Max}), nouvel essai à {RetryAt:u}", RequestService.Describe(primary), attempt, MaxImportAttempts, retryAt);
                await _requests.UpdateActiveAsync(IsMine, r =>
                {
                    r.Status = RequestStatus.CompletedOnTorbox;
                    r.ImportProgress = null;
                    r.ImportAttempts = attempt;
                    r.RetryImportAfter = retryAt;
                    r.LastError = ex.Message;
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _logger.LogError(ex, "Import de « {Title} » en échec après {Attempts} essais", RequestService.Describe(primary), attempt);
            await _requests.UpdateActiveAsync(IsMine, r =>
            {
                r.Status = RequestStatus.ImportFailed;
                r.ImportAttempts = attempt;
                r.RetryImportAfter = null;
                r.LastError = ex.Message;
            }, CancellationToken.None).ConfigureAwait(false);
            await _notifications.NotifyAdminsAsync(
                $"Échec du rangement de « {RequestService.Describe(primary)} » après {attempt} essais : {ex.Message}", primary.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Attente avant le nouvel essai : 5 min après le premier échec, 30 min après le deuxième.</summary>
    internal static TimeSpan RetryDelay(int failedAttempts) => failedAttempts <= 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);

    /// <summary>
    /// Lit les fichiers sur Torbox, résout les dossiers et calcule le plan d'import. Renvoie aussi les épisodes
    /// contenus dans le torrent (séries). <paramref name="replacing"/> : remplacement d'un média, rangé à la place
    /// de ses anciens fichiers (<paramref name="replacedPaths"/>, qui peuvent être écrasés).
    /// </summary>
    private async Task<(string Folder, ImportPlan Plan, long TorrentId, IReadOnlyList<(int Season, int Episode)> Episodes)> PrepareAsync(
        MediaRequest request, bool replacing, IReadOnlySet<string> replacedPaths, CancellationToken ct)
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
            throw new ImportException(request.MediaKind switch
            {
                MediaKind.Movie => "Aucun dossier de films : renseignez-le dans la configuration du plugin.",
                MediaKind.Book => "Aucun dossier de livres : renseignez-le dans la configuration du plugin.",
                _ => "Aucun dossier de séries : renseignez-le dans la configuration du plugin.",
            });
        }

        if (!Directory.Exists(root))
        {
            throw new ImportException($"Le dossier « {root} » est introuvable depuis le serveur Jellyfin.");
        }

        var ids = RequestService.ProviderIdsOf(request);
        IReadOnlyList<(int Season, int Episode)> episodes = Array.Empty<(int, int)>();
        ImportTarget target;
        if (request.MediaKind == MediaKind.Movie)
        {
            // Remplacement : le nouveau fichier va dans le dossier actuel du film, comme une mise à niveau Radarr.
            target = new ImportTarget
            {
                Folder = (replacing ? OwnFolderOf(replacedPaths, request.MediaKind) : null)
                    ?? Path.Combine(root, LibraryNaming.MovieFolder(request.MediaTitle, request.Year, request.TmdbId)),
                IncludeSubtitles = _config.Current.ImportSubtitles,
            };
        }
        else if (request.MediaKind == MediaKind.Book)
        {
            target = new ImportTarget
            {
                Folder = (replacing ? OwnFolderOf(replacedPaths, request.MediaKind) : null)
                    ?? Path.Combine(root, LibraryNaming.BookName(request.MediaTitle, request.Year)),
            };
        }
        else
        {
            // Série déjà présente : on range dans son dossier actuel, comme Sonarr.
            var seriesFolder = _library.GetSeriesFolder(ids)
                ?? Path.Combine(root, LibraryNaming.SeriesFolder(request.MediaTitle, request.Year, request.TmdbId));
            episodes = ImportPlanner.FindEpisodes(request, torrent.Files);
            var titles = await _titles.GetTitlesAsync(ids, episodes, ct).ConfigureAwait(false);
            target = new ImportTarget
            {
                Folder = seriesFolder,
                SeasonFolder = s => _library.GetSeasonFolder(ids, s) ?? Path.Combine(seriesFolder, LibraryNaming.SeasonFolder(s)),
                EpisodeTitles = titles,

                // Remplacement d'une saison : tous ses épisodes sont retéléchargés, même ceux déjà présents.
                EpisodeExists = replacing ? (_, _) => false : (s, e) => _library.CountEpisodes(ids, s, e) > 0,
                IncludeSubtitles = _config.Current.ImportSubtitles,
            };
        }

        var plan = ImportPlanner.Plan(request, torrent.Files, target);
        EnsureInside(target.Folder, plan);
        EnsureNoOverwrite(plan, replacedPaths);
        EnsureFreeSpace(target.Folder, root, plan);
        return (target.Folder, plan, torrent.Id, episodes);
    }

    /// <summary>
    /// Dossier propre au média remplacé (celui de son ancien fichier), ou null s'il est rangé directement à la racine
    /// d'une bibliothèque (le nouveau fichier prend alors un dossier à son nom).
    /// </summary>
    private string? OwnFolderOf(IReadOnlySet<string> replacedPaths, MediaKind kind)
    {
        var folder = replacedPaths.Select(Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d));
        if (folder is null || !Directory.Exists(folder) || IsLibraryRoot(folder, kind))
        {
            return null;
        }

        return folder;
    }

    private bool IsLibraryRoot(string folder, MediaKind kind)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return _library.GetLibraryFolders(kind).Append(ResolveRoot(kind))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Any(r => PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)), full));
    }

    /// <summary>Comparaison des chemins : insensible à la casse sous Windows seulement.</summary>
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Fichiers « compagnons » supprimés avec l'ancien fichier d'un média remplacé (sous-titres, .nfo, vignette).</summary>
    private static readonly string[] SidecarExtensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".nfo", ".jpg", ".png"];

    /// <summary>
    /// Supprime les anciens fichiers d'un média remplacé, une fois les nouveaux rangés : sauf ceux écrasés par un
    /// nouveau fichier du même nom, et, pour une série, ceux dont les épisodes ne sont pas tous dans le nouveau torrent.
    /// Renvoie les dossiers touchés (à signaler à Jellyfin) et les problèmes rencontrés (jamais d'exception : les
    /// nouveaux fichiers sont déjà en place).
    /// </summary>
    private (List<string> Folders, List<string> Problems) DeleteReplacedFiles(
        IReadOnlyList<ReplacedFile> replaced, ImportPlan plan, IReadOnlyCollection<(int Season, int Episode)> episodes)
    {
        var newFiles = new HashSet<string>(plan.Items.Select(i => Path.GetFullPath(i.Destination)), PathComparer);
        var covered = episodes.ToHashSet();
        var folders = new List<string>();
        var problems = new List<string>();
        foreach (var old in replaced)
        {
            var path = Path.GetFullPath(old.Path);
            var dir = Path.GetDirectoryName(path);
            if (newFiles.Contains(path) || dir is null)
            {
                continue;
            }

            if (old.Season is { } s && old.Episode is { } first)
            {
                var last = old.EpisodeEnd is { } end && end >= first ? Math.Min(end, first + 50) : first;
                if (!Enumerable.Range(first, last - first + 1).All(e => covered.Contains((s, e))))
                {
                    problems.Add($"« {Path.GetFileName(path)} » conservé (épisode absent du nouveau torrent)");
                    continue;
                }
            }

            try
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var sidecars = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir)
                        .Where(f => !PathComparer.Equals(f, path) && !newFiles.Contains(Path.GetFullPath(f)))
                        .Where(f =>
                        {
                            var name = Path.GetFileName(f);
                            return (name.StartsWith(stem + ".", StringComparison.Ordinal) || name.StartsWith(stem + "-", StringComparison.Ordinal))
                                && SidecarExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);
                        })
                        .ToList()
                    : new List<string>();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                foreach (var sidecar in sidecars)
                {
                    File.Delete(sidecar);
                }

                _logger.LogInformation("Remplacement : ancien fichier {Path} supprimé ({Count} fichier(s) associé(s))", path, sidecars.Count);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any() && !IsLibraryRoot(dir, MediaKind.Movie)
                    && !IsLibraryRoot(dir, MediaKind.Series) && !IsLibraryRoot(dir, MediaKind.Book))
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir) ?? dir;
                }

                folders.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Remplacement : suppression de l'ancien fichier {Path} impossible", path);
                problems.Add($"l'ancien fichier « {path} » n'a pas pu être supprimé ({ex.Message})");
            }
        }

        return (folders, problems);
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
    /// de même taille, est considéré comme déjà rangé), sauf s'il s'agit de l'ancien fichier d'un média remplacé.
    /// </summary>
    private static void EnsureNoOverwrite(ImportPlan plan, IReadOnlySet<string> replacedPaths)
    {
        foreach (var item in plan.Items)
        {
            if (File.Exists(item.Destination) && new FileInfo(item.Destination).Length != item.File.Size
                && !replacedPaths.Contains(Path.GetFullPath(item.Destination)))
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

    private async Task DownloadAllAsync(MediaRequest primary, ImportPlan plan, long torrentId, Func<MediaRequest, bool> isMine, IReadOnlySet<string> replacedPaths, CancellationToken ct)
    {
        long total = Math.Max(1, plan.TotalBytes);
        var items = plan.Items;
        var bytes = new long[items.Count];
        var clock = Stopwatch.StartNew();
        long lastReportTicks = 0;

        void OnBytes(int index, long written)
        {
            Interlocked.Exchange(ref bytes[index], written);
            long now = clock.Elapsed.Ticks;
            long last = Interlocked.Read(ref lastReportTicks);
            if (now - last < ProgressInterval.Ticks || Interlocked.CompareExchange(ref lastReportTicks, now, last) != last)
            {
                return;
            }

            long done = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                done += Interlocked.Read(ref bytes[i]);
            }

            _ = ReportProgressAsync(isMine, Math.Clamp(done / (double)total, 0, 0.99));
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, async (index, token) =>
        {
            var item = items[index];

            // L'ancien fichier d'un média remplacé, au même nom, n'est jamais pris pour le nouveau : il est écrasé une fois celui-ci complet.
            var overwrite = replacedPaths.Contains(Path.GetFullPath(item.Destination));
            if (!overwrite && File.Exists(item.Destination) && item.File.Size > 0 && new FileInfo(item.Destination).Length == item.File.Size)
            {
                Interlocked.Exchange(ref bytes[index], item.File.Size);
                return;
            }

            // La limite est globale : les fichiers des autres imports en cours comptent aussi.
            using var slot = await _gate.EnterAsync(token).ConfigureAwait(false);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var url = await _torbox.RequestDownloadUrlAsync(torrentId, item.File.Id, token).ConfigureAwait(false);
                    await _downloader.DownloadAsync(url, item.Destination, item.File.Size, written => OnBytes(index, written), token, overwrite).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < AttemptsPerFile && !token.IsCancellationRequested
                    && ex is ImportException or TorboxException or HttpRequestException or IOException)
                {
                    // Coupure réseau ou lien expiré : nouveau lien, reprise là où le fichier partiel s'est arrêté.
                    _logger.LogWarning(ex, "Téléchargement de {File} interrompu (essai {Attempt}/{Max}), nouvelle tentative", item.File.Name, attempt, AttemptsPerFile);
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), token).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);

        _logger.LogDebug("Import de {Title} : {Bytes} octets en {Elapsed}", RequestService.Describe(primary), bytes.Sum(), clock.Elapsed);
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
