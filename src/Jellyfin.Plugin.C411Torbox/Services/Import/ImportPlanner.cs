using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>Erreur d'import à afficher telle quelle (message en français).</summary>
public class ImportException : Exception
{
    public ImportException(string message)
        : base(message)
    {
    }

    public ImportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Un fichier du torrent et son emplacement final dans la bibliothèque.</summary>
public sealed record ImportItem(TorboxFile File, string Destination);

/// <summary>Résultat du choix des fichiers : ce qui sera téléchargé et ce qui est ignoré (avec la raison).</summary>
public sealed record ImportPlan(IReadOnlyList<ImportItem> Items, IReadOnlyList<string> Skipped)
{
    public long TotalBytes => Items.Sum(i => Math.Max(0, i.File.Size));
}

/// <summary>Où ranger les fichiers d'une demande (dossiers déjà résolus par l'appelant).</summary>
public sealed class ImportTarget
{
    /// <summary>Dossier du film, du livre, ou dossier de la série.</summary>
    public required string Folder { get; init; }

    /// <summary>Dossier d'une saison (dossier existant dans Jellyfin, sinon « Season 01 » dans le dossier de la série).</summary>
    public Func<int, string>? SeasonFolder { get; init; }

    /// <summary>Titres d'épisodes connus, par (saison, épisode).</summary>
    public IReadOnlyDictionary<(int Season, int Episode), string> EpisodeTitles { get; init; } = new Dictionary<(int, int), string>();

    /// <summary>Épisode déjà présent dans la bibliothèque (non retéléchargé lors d'un pack de saison).</summary>
    public Func<int, int, bool> EpisodeExists { get; init; } = (_, _) => false;

    public bool IncludeSubtitles { get; init; } = true;
}

/// <summary>
/// Choisit les fichiers d'un torrent à ranger et calcule leur nom (logique pure, sans accès disque ni réseau).
/// Le média est connu d'avance (fiche TMDB de la demande) : seuls les numéros d'épisode sont lus dans les noms.
/// </summary>
public static class ImportPlanner
{
    /// <summary>Épisodes (saison, épisode) contenus dans le torrent pour la demande, avant tout filtrage « déjà présent ».</summary>
    public static IReadOnlyList<(int Season, int Episode)> FindEpisodes(MediaRequest request, IReadOnlyList<TorboxFile> files)
        => request.MediaKind != MediaKind.Series
            ? Array.Empty<(int, int)>()
            : SelectEpisodeFiles(request, files, new List<string>())
                .SelectMany(x => x.Episodes.Select(e => (x.Season, e)))
                .Distinct()
                .ToList();

    public static ImportPlan Plan(MediaRequest request, IReadOnlyList<TorboxFile> files, ImportTarget target)
        => request.MediaKind switch
        {
            MediaKind.Movie => PlanMovie(request, files, target),
            MediaKind.Book => PlanBook(request, files, target),
            _ => PlanEpisodes(request, files, target),
        };

    private static bool IsCandidateVideo(TorboxFile f)
        => ReleaseParser.IsVideo(f.Name) && !ReleaseParser.IsInExtraFolder(f.Name) && !ReleaseParser.IsSample(f.Name, f.Size);

    private static string QualityOf(TorboxFile f, MediaRequest r)
    {
        var q = ReleaseParser.ParseQuality(ReleaseParser.FileName(f.Name));
        return q.Contains('-', StringComparison.Ordinal) ? q : MoreComplete(q, ReleaseParser.ParseQuality(r.TorrentTitle));
    }

    /// <summary>Garde la qualité la plus précise (« Bluray-1080p » plutôt que « 1080p »).</summary>
    private static string MoreComplete(string a, string b) => b.Length > a.Length ? b : a;

    private static ImportPlan PlanMovie(MediaRequest request, IReadOnlyList<TorboxFile> files, ImportTarget target)
    {
        var skipped = new List<string>();
        var videos = files.Where(IsCandidateVideo).OrderByDescending(f => f.Size).ToList();
        var main = videos.FirstOrDefault()
            ?? throw new ImportException("Aucun fichier vidéo exploitable dans le torrent.");
        skipped.AddRange(videos.Skip(1).Select(f => ReleaseParser.FileName(f.Name) + " : vidéo secondaire"));

        var baseName = LibraryNaming.MovieFileBase(request.MediaTitle, request.Year, QualityOf(main, request));
        var items = new List<ImportItem> { new(main, Path.Combine(target.Folder, baseName + ReleaseParser.Extension(main.Name))) };
        if (target.IncludeSubtitles)
        {
            AddSubtitles(items, files.Where(f => ReleaseParser.IsSubtitle(f.Name) && !ReleaseParser.IsInExtraFolder(f.Name)), target.Folder, baseName);
        }

        return new ImportPlan(items, skipped);
    }

    /// <summary>
    /// Un seul livre : renommé « Titre (Année) ». Un pack (intégrale, collection, numéros de presse) :
    /// chaque fichier garde son nom, à plat dans le dossier du livre.
    /// </summary>
    private static ImportPlan PlanBook(MediaRequest request, IReadOnlyList<TorboxFile> files, ImportTarget target)
    {
        var books = files.Where(f => ReleaseParser.IsBook(f.Name) && !ReleaseParser.IsInExtraFolder(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (books.Count == 0)
        {
            throw new ImportException("Aucun livre exploitable (epub, pdf, cbz, cbr…) dans le torrent.");
        }

        var skipped = files.Except(books).Select(f => ReleaseParser.FileName(f.Name) + " : pas un livre").ToList();
        if (books.Count == 1)
        {
            var single = books[0];
            var name = LibraryNaming.BookName(request.MediaTitle, request.Year) + ReleaseParser.Extension(single.Name);
            return new ImportPlan(new[] { new ImportItem(single, Path.Combine(target.Folder, name)) }, skipped);
        }

        var items = new List<ImportItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in books)
        {
            var file = ReleaseParser.FileName(book.Name);
            var ext = ReleaseParser.Extension(file);
            var baseName = LibraryNaming.Clean(file[..^ext.Length]);
            var dest = Path.Combine(target.Folder, baseName + ext);
            for (int n = 2; !used.Add(dest); n++)
            {
                dest = Path.Combine(target.Folder, baseName + " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + ext);
            }

            items.Add(new ImportItem(book, dest));
        }

        return new ImportPlan(items, skipped);
    }

    private static ImportPlan PlanEpisodes(MediaRequest request, IReadOnlyList<TorboxFile> files, ImportTarget target)
    {
        var skipped = new List<string>();
        var selected = SelectEpisodeFiles(request, files, skipped);
        if (selected.Count == 0)
        {
            throw new ImportException(request.Scope == RequestScope.Episode
                ? "L'épisode demandé est introuvable dans le torrent."
                : "Aucun épisode de la saison demandée n'a été trouvé dans le torrent.");
        }

        var seasonFolder = target.SeasonFolder ?? (s => Path.Combine(target.Folder, LibraryNaming.SeasonFolder(s)));
        var items = new List<ImportItem>();
        var subtitles = target.IncludeSubtitles
            ? files.Where(f => ReleaseParser.IsSubtitle(f.Name) && !ReleaseParser.IsInExtraFolder(f.Name)).ToList()
            : new List<TorboxFile>();

        foreach (var (file, season, episodes) in selected)
        {
            if (request.Scope == RequestScope.Season && episodes.All(e => target.EpisodeExists(season, e)))
            {
                skipped.Add(ReleaseParser.FileName(file.Name) + " : déjà dans la bibliothèque");
                continue;
            }

            var title = string.Join(" + ", episodes
                .Select(e => target.EpisodeTitles.GetValueOrDefault((season, e)))
                .Where(t => !string.IsNullOrWhiteSpace(t)));
            var baseName = LibraryNaming.EpisodeFileBase(request.MediaTitle, season, episodes, title, QualityOf(file, request));
            var folder = seasonFolder(season);
            items.Add(new ImportItem(file, Path.Combine(folder, baseName + ReleaseParser.Extension(file.Name))));

            // Sous-titres du même épisode (même SxxEyy dans le nom ou dans le dossier, ex. « Subs/Show.S01E02/2_French.srt »).
            var mine = subtitles.Where(s => ReleaseParser.ParseEpisode(s.Name.Replace('/', ' ')) is { } n
                    && (n.Season ?? season) == season && n.Episodes.Contains(episodes[0]))
                .ToList();
            if (mine.Count == 0 && selected.Count == 1)
            {
                mine = subtitles;
            }

            AddSubtitles(items, mine, folder, baseName);
        }

        if (items.Count == 0)
        {
            throw new ImportException("Tous les épisodes du torrent sont déjà dans la bibliothèque.");
        }

        return new ImportPlan(items, skipped);
    }

    /// <summary>Fichiers vidéo retenus avec leurs numéros, un seul fichier par épisode (le plus gros).</summary>
    private static List<(TorboxFile File, int Season, IReadOnlyList<int> Episodes)> SelectEpisodeFiles(
        MediaRequest request, IReadOnlyList<TorboxFile> files, List<string> skipped)
    {
        int season = request.Season ?? 1;
        var videos = files.Where(IsCandidateVideo).ToList();
        var parsed = videos.Select(f => (File: f, Number: ReleaseParser.ParseEpisode(f.Name))).ToList();
        var result = new List<(TorboxFile File, int Season, IReadOnlyList<int> Episodes)>();

        if (request.Scope == RequestScope.Episode && request.Episode is { } wanted)
        {
            var match = parsed
                .Where(p => p.Number is { } n && (n.Season ?? season) == season && n.Episodes.Contains(wanted))
                .OrderByDescending(p => p.File.Size)
                .Select(p => (p.File, season, p.Number!.Episodes))
                .FirstOrDefault();
            if (match.File is not null)
            {
                result.Add(match);
            }
            else if (videos.Count == 1)
            {
                // Un seul fichier vidéo : c'est l'épisode demandé, même si son nom ne le dit pas.
                result.Add((videos[0], season, new[] { wanted }));
            }

            return result;
        }

        var seen = new HashSet<int>();
        foreach (var p in parsed.OrderByDescending(p => p.File.Size))
        {
            var name = ReleaseParser.FileName(p.File.Name);
            if (p.Number is null || p.Number.Episodes.Count == 0)
            {
                skipped.Add(name + " : numéro d'épisode introuvable");
                continue;
            }

            if ((p.Number.Season ?? season) != season)
            {
                skipped.Add(name + " : autre saison");
                continue;
            }

            if (p.Number.Episodes.Any(e => !seen.Add(e)))
            {
                skipped.Add(name + " : doublon");
                continue;
            }

            result.Add((p.File, season, p.Number.Episodes));
        }

        return result.OrderBy(r => r.Episodes[0]).ToList();
    }

    private static void AddSubtitles(List<ImportItem> items, IEnumerable<TorboxFile> subtitles, string folder, string baseName)
    {
        var used = new HashSet<string>(items.Select(i => i.Destination), StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subtitles.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var suffix = ReleaseParser.SubtitleSuffix(sub.Name);
            var ext = ReleaseParser.Extension(sub.Name);
            var dest = Path.Combine(folder, baseName + suffix + ext);
            for (int n = 2; !used.Add(dest); n++)
            {
                dest = Path.Combine(folder, baseName + suffix + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);
            }

            items.Add(new ImportItem(sub, dest));
        }
    }
}
