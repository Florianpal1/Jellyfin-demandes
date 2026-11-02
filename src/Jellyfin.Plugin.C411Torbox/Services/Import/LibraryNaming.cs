using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>
/// Nommage « standard » Radarr / Sonarr, avec l'identifiant TMDB dans le nom de dossier pour
/// une identification sans ambiguïté par Jellyfin :
/// <list type="bullet">
/// <item>Films/Titre (2010) [tmdbid-27205]/Titre (2010) Bluray-1080p.mkv</item>
/// <item>Séries/Titre (2008) [tmdbid-1396]/Season 01/Titre - S01E01 - Nom de l'épisode WEBDL-1080p.mkv</item>
/// </list>
/// </summary>
public static partial class LibraryNaming
{
    /// <summary>Rend un nom utilisable comme nom de fichier sur tous les systèmes (partages SMB compris).</summary>
    public static string Clean(string? name)
    {
        var s = (name ?? string.Empty).Replace(": ", " - ").Replace(':', '-').Replace('/', '-').Replace('\\', '-');
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                continue;
            }

            sb.Append(c);
        }

        var cleaned = SpacesRegex().Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "Sans titre" : cleaned;
    }

    public static string MovieFolder(string title, int? year, string? tmdbId)
        => Clean(WithYear(title, year)) + TmdbTag(tmdbId);

    public static string MovieFileBase(string title, int? year, string quality)
        => Clean(WithYear(title, year) + (quality.Length > 0 ? " " + quality : string.Empty));

    public static string SeriesFolder(string title, int? year, string? tmdbId)
        => Clean(WithYear(title, year)) + TmdbTag(tmdbId);

    public static string SeasonFolder(int season)
        => season == 0 ? "Specials" : "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>« S01E02 », ou « S01E02-03 » pour un fichier multi-épisodes (style « Extend » de Sonarr).</summary>
    public static string EpisodeCode(int season, IReadOnlyList<int> episodes)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"S{season:00}E{episodes[0]:00}");
        foreach (var e in episodes.Skip(1))
        {
            sb.Append(CultureInfo.InvariantCulture, $"-{e:00}");
        }

        return sb.ToString();
    }

    public static string EpisodeFileBase(string seriesTitle, int season, IReadOnlyList<int> episodes, string? episodeTitle, string quality)
    {
        var name = seriesTitle + " - " + EpisodeCode(season, episodes);
        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            name += " - " + episodeTitle.Trim();
        }

        if (quality.Length > 0)
        {
            name += " " + quality;
        }

        return Clean(name);
    }

    private static string WithYear(string title, int? year)
        => year is > 0 ? string.Create(CultureInfo.InvariantCulture, $"{title} ({year})") : title;

    private static string TmdbTag(string? tmdbId)
        => string.IsNullOrWhiteSpace(tmdbId) ? string.Empty : " [tmdbid-" + Clean(tmdbId) + "]";

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
