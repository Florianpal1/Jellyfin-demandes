using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>Numéros de saison et d'épisode(s) lus dans un nom de fichier.</summary>
public sealed record EpisodeNumber(int? Season, IReadOnlyList<int> Episodes);

/// <summary>Analyse des noms de release (épisodes, qualité, type de fichier), à la manière de Sonarr / Radarr.</summary>
public static partial class ReleaseParser
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg",
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup",
    };

    /// <summary>Livres numériques, BD / comics (archives d'images) et presse.</summary>
    private static readonly HashSet<string> BookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".fb2", ".djvu", ".cbz", ".cbr", ".cb7", ".cbt",
    };

    /// <summary>Dossiers de bonus ignorés (comme Radarr / Sonarr).</summary>
    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "bonus", "bonuses", "featurettes", "featurette", "behind the scenes", "deleted scenes",
        "interviews", "trailers", "trailer", "sample", "samples", "screens", "screenshots", "proof",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr", ["francais"] = "fr", ["français"] = "fr",
        ["vf"] = "fr", ["vff"] = "fr", ["vfi"] = "fr", ["vfq"] = "fr", ["truefrench"] = "fr",
        ["en"] = "en", ["eng"] = "en", ["english"] = "en", ["anglais"] = "en",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es", ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["german"] = "de",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it", ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt",
        ["nl"] = "nl", ["dut"] = "nl", ["dutch"] = "nl", ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Extension(path));

    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Extension(path));

    public static bool IsBook(string path) => BookExtensions.Contains(Extension(path));

    /// <summary>Extension en minuscules, point compris (« .mkv »).</summary>
    public static string Extension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }

    /// <summary>Nom du fichier sans les dossiers (les chemins Torbox utilisent « / »).</summary>
    public static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Fichier situé dans un dossier de bonus (« Extras », « Featurettes », « Sample »…).</summary>
    public static bool IsInExtraFolder(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Take(parts.Length - 1).Any(p => ExtraFolders.Contains(p.Trim()));
    }

    /// <summary>Extrait vidéo (« sample ») : à ne pas ranger.</summary>
    public static bool IsSample(string path, long size)
        => SampleRegex().IsMatch(FileName(path)) && (size <= 0 || size < 500L * 1024 * 1024);

    /// <summary>Saison et épisode(s) : S01E02, S01E02E03, S01E02-E03, S01E02-03, 1x02, ou seulement E02 / « 02 - Titre ».</summary>
    public static EpisodeNumber? ParseEpisode(string path)
    {
        var name = FileName(path);
        var m = SxxEyyRegex().Match(name);
        if (m.Success)
        {
            var episodes = new List<int> { Int(m.Groups["e1"].Value) };
            foreach (Capture c in m.Groups["more"].Captures)
            {
                episodes.Add(Int(c.Value));
            }

            if (m.Groups["last"].Success)
            {
                var last = Int(m.Groups["last"].Value);
                for (int e = episodes[^1] + 1; e <= last && e - episodes[0] < 50; e++)
                {
                    episodes.Add(e);
                }
            }

            return new EpisodeNumber(Int(m.Groups["s"].Value), episodes.Distinct().ToList());
        }

        m = CrossRegex().Match(name);
        if (m.Success)
        {
            var first = Int(m.Groups["e1"].Value);
            var episodes = new List<int> { first };
            if (m.Groups["last"].Success)
            {
                for (int e = first + 1; e <= Int(m.Groups["last"].Value) && e - first < 50; e++)
                {
                    episodes.Add(e);
                }
            }

            return new EpisodeNumber(Int(m.Groups["s"].Value), episodes);
        }

        m = EpisodeOnlyRegex().Match(name);
        if (m.Success)
        {
            return new EpisodeNumber(null, [Int(m.Groups["e"].Value)]);
        }

        m = LeadingNumberRegex().Match(name);
        if (m.Success)
        {
            return new EpisodeNumber(null, [Int(m.Groups["e"].Value)]);
        }

        return null;
    }

    /// <summary>Qualité au format Radarr / Sonarr : « Bluray-1080p », « WEBDL-2160p », « Remux-1080p », « HDTV-720p »…</summary>
    public static string ParseQuality(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var n = name.ToLowerInvariant();
        string? resolution = ResolutionRegex().Match(n) is { Success: true } r
            ? r.Groups[1].Value switch
            {
                "4k" or "uhd" or "2160" => "2160p",
                var v => v + "p",
            }
            : null;

        string? source = null;
        if (Regex.IsMatch(n, @"\bremux\b"))
        {
            source = "Remux";
        }
        else if (Regex.IsMatch(n, @"\b(blu-?ray|bdrip|brrip|bdmv|bd25|bd50|hdlight|mhd)\b"))
        {
            source = "Bluray";
        }
        else if (Regex.IsMatch(n, @"\bweb-?rip\b"))
        {
            source = "WEBRip";
        }
        else if (Regex.IsMatch(n, @"\b(web-?dl|web|amzn|nf|dsnp|hmax|atvp)\b"))
        {
            source = "WEBDL";
        }
        else if (Regex.IsMatch(n, @"\b(hdtv|pdtv|tvrip)\b"))
        {
            source = "HDTV";
        }
        else if (Regex.IsMatch(n, @"\b(dvdrip|dvd|dvdr|dvd5|dvd9)\b"))
        {
            source = "DVD";
        }

        return (source, resolution) switch
        {
            (null, null) => string.Empty,
            (null, var res) => res!,
            ("DVD", _) => "DVD",
            (var src, null) => src,
            (var src, var res) => src + "-" + res,
        };
    }

    /// <summary>
    /// Suffixe de langue d'un sous-titre (« .fr », « .en.forced »…) d'après son nom, ou vide.
    /// Ex. : « Film.FRENCH.srt » → « .fr », « 2_English.srt » → « .en », « film.fr.forced.srt » → « .fr.forced ».
    /// </summary>
    public static string SubtitleSuffix(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        var stem = (dot > 0 ? name[..dot] : name).ToLowerInvariant();
        var tokens = TokenRegex().Split(stem).Where(t => t.Length > 0).ToList();

        string? language = null;
        for (int i = tokens.Count - 1; i >= 0 && i >= tokens.Count - 4; i--)
        {
            if (Languages.TryGetValue(tokens[i], out var code))
            {
                language = code;
                break;
            }
        }

        var flags = new List<string>();
        if (tokens.Contains("forced") || tokens.Contains("force"))
        {
            flags.Add("forced");
        }

        if (tokens.Contains("sdh") || tokens.Contains("cc") || tokens.Contains("hi"))
        {
            flags.Add("sdh");
        }

        var parts = new List<string>();
        if (language is not null)
        {
            parts.Add(language);
        }

        parts.AddRange(flags);
        return parts.Count == 0 ? string.Empty : "." + string.Join('.', parts);
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![a-z0-9])sample(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRegex();

    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]?e(?<e1>\d{1,3})(?:[ ._]?e(?<more>\d{1,3}))*(?:-e?(?<last>\d{1,3})(?![0-9a-z]))?", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyyRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e1>\d{2,3})(?:-(?:\d{1,2}x)?(?<last>\d{2,3}))?(?![0-9p])", RegexOptions.IgnoreCase)]
    private static partial Regex CrossRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:e|ep|episode)[ ._-]?(?<e>\d{1,3})(?![0-9p])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyRegex();

    [GeneratedRegex(@"^(?<e>\d{1,3})(?:[ ._]*-[ ._]*|[ ._]+)(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(2160|1080|720|576|480|4k|uhd)[pi]?(?![a-z0-9])")]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"[^a-zà-ÿ0-9]+")]
    private static partial Regex TokenRegex();
}
