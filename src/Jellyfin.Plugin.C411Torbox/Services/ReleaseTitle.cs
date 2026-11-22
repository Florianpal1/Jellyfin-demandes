using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Titre, année, saison et épisode lus dans un nom de release (« Dune.Part.Two.2024.MULTi.1080p.WEB.x264-GRP »).</summary>
public sealed record ReleaseTitleInfo(string Name, int? Year, int? Season, int? Episode);

/// <summary>
/// Lecture d'un nom de release, pour retrouver la fiche du média quand l'indexeur ne donne pas son identifiant.
/// Le titre est ce qui précède l'année, la saison ou la première étiquette technique (MULTi, 1080p, WEB…).
/// </summary>
public static partial class ReleaseTitle
{
    public static ReleaseTitleInfo Parse(string? release)
    {
        var text = SeparatorRegex().Replace(release ?? string.Empty, " ").Trim();
        text = SpacesRegex().Replace(text, " ");

        int cut = text.Length;
        int? season = null, episode = null, year = null;

        var s = SeasonRegex().Match(text);
        if (s.Success)
        {
            cut = Math.Min(cut, s.Index);
            season = Int(s.Groups["s"].Success ? s.Groups["s"].Value : s.Groups["s2"].Value);
            episode = s.Groups["e"].Success ? Int(s.Groups["e"].Value) : null;
        }

        // Une année en tête est un titre (« 1917 », « 2001 l'odyssée de l'espace ») : seule une année suivante compte.
        foreach (Match y in YearRegex().Matches(text))
        {
            if (y.Index > 0)
            {
                year = Int(y.Groups["y"].Value);
                cut = Math.Min(cut, y.Index);
                break;
            }
        }

        var tag = TagRegex().Match(text);
        if (tag.Success && tag.Index > 0)
        {
            cut = Math.Min(cut, tag.Index);
        }

        var name = text[..cut].Trim().TrimEnd('-', '(', '[', ' ').Trim();
        if (name.Length == 0)
        {
            name = text;
        }

        return new ReleaseTitleInfo(name, year, season, episode);
    }

    private static int Int(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[._]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\b(?:S(?<s>\d{1,2})(?:\s?E(?<e>\d{1,3}))?|(?:Saison|Season)\s?(?<s2>\d{1,2}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"[\(\[]?\b(?<y>19\d{2}|20\d{2})\b[\)\]]?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(
        @"\b(?:MULTi|VFF|VFQ|VFI|VF2|VOF|VOSTFR|VOST|TRUEFRENCH|FRENCH|SUBFRENCH|iNTEGRALE|INTEGRALE|COMPLETE|2160p|1080p|720p|480p|4K|UHD|HDR|WEB|WEBRip|WEB-DL|BluRay|BDRip|HDTV|REMUX|x264|x265|H264|H265|HEVC)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
}
