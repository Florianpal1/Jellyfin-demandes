using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services.Import;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Écarte les torrents sans rapport avec le média demandé. L'indexeur peut ignorer un paramètre
/// de recherche (identifiant, saison…) ou faire une recherche textuelle approximative, et renvoyer
/// alors des releases quelconques.
/// <list type="bullet">
/// <item>Identifiant TMDB / IMDb / TVDB renvoyé par l'indexeur : il fait foi (différent → écarté).</item>
/// <item>Sinon le nom de la release doit commencer par le titre du média (ou un titre alternatif),
/// sans tenir compte des accents ni de la ponctuation ; pour un film, l'année doit correspondre (± 1).</item>
/// <item>Séries : la saison (et l'épisode) indiqués dans le nom doivent correspondre.</item>
/// </list>
/// </summary>
public sealed partial class ReleaseMatcher
{
    private readonly TorrentSearchRequest _request;
    private readonly Dictionary<string, string> _ids;
    private readonly List<string[]> _names;

    public ReleaseMatcher(TorrentSearchRequest request)
    {
        _request = request;
        _ids = LibraryLookup.FilterIds(request.ProviderIds);
        _names = new[] { request.Name }
            .Concat(request.AlternateNames)
            .Select(Tokenize)
            .Where(t => t.Length > 0)
            .DistinctBy(t => string.Join(' ', t))
            .ToList();
    }

    public bool IsRelevant(TorrentResult result)
    {
        var byId = CompareIds(result);
        if (byId == false)
        {
            return false;
        }

        if (_request.Kind == MediaKind.Series && !EpisodeMatches(result.Title))
        {
            return false;
        }

        if (byId == true)
        {
            return true;
        }

        var tokens = Tokenize(StripLeadingTags(result.Title));
        foreach (var name in _names)
        {
            // Livres : souvent « Auteur - Titre », le titre peut être n'importe où dans le nom.
            if (_request.Kind == MediaKind.Book)
            {
                if (tokens.AsSpan().IndexOf(name) >= 0)
                {
                    return true;
                }

                continue;
            }

            if (StartsWith(tokens, name) && (_request.Kind != MediaKind.Movie || YearMatches(tokens.AsSpan(name.Length))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>true : même identifiant ; false : identifiant différent ; null : rien à comparer.</summary>
    private bool? CompareIds(TorrentResult result)
    {
        bool? verdict = null;
        foreach (var (provider, theirs) in new[] { ("Tmdb", result.TmdbId), ("Imdb", result.ImdbId), ("Tvdb", result.TvdbId) })
        {
            if (string.IsNullOrWhiteSpace(theirs) || !_ids.TryGetValue(provider, out var ours))
            {
                continue;
            }

            if (!NormalizeId(ours).Equals(NormalizeId(theirs), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            verdict = true;
        }

        return verdict;
    }

    /// <summary>« tt0068646 », « 0068646 » et « 68646 » désignent le même film IMDb.</summary>
    private static string NormalizeId(string id)
    {
        var s = id.Trim();
        if (s.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        s = s.TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }

    private bool EpisodeMatches(string title)
    {
        var season = _request.Season!.Value;
        var parsed = ReleaseParser.ParseEpisode(title.Replace('/', ' '));
        if (parsed?.Season is int s)
        {
            // Release d'épisode(s) : il faut la bonne saison, et l'épisode demandé (une demande de pack
            // de saison n'affiche pas les épisodes isolés).
            return s == season && _request.Episode is int ep && parsed.Episodes.Contains(ep);
        }

        // Pack de saison(s) : « S02 », « S01-S03 », « Saison 2 », « Season 1 à 3 »… Sans indication
        // (« Intégrale », « Complete »), on garde.
        var ranges = SeasonRegex().Matches(title)
            .Select(m => (First: int.Parse(m.Groups["s1"].Value, CultureInfo.InvariantCulture),
                Last: m.Groups["s2"].Success ? int.Parse(m.Groups["s2"].Value, CultureInfo.InvariantCulture) : (int?)null))
            .ToList();
        return ranges.Count == 0 || ranges.Any(r => season >= r.First && season <= (r.Last ?? r.First));
    }

    /// <summary>Années (1900-2099) présentes après le titre : au moins une doit valoir l'année du film ± 1.</summary>
    private bool YearMatches(ReadOnlySpan<string> rest)
    {
        if (_request.Year is not int year)
        {
            return true;
        }

        var found = false;
        foreach (var token in rest)
        {
            if (token.Length == 4 && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2099)
            {
                if (Math.Abs(y - year) <= 1)
                {
                    return true;
                }

                found = true;
            }
        }

        return !found;
    }

    private static bool StartsWith(string[] tokens, string[] prefix)
        => tokens.Length >= prefix.Length && tokens.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    /// <summary>Retire les étiquettes de tête : « [Team] Titre… ».</summary>
    private static string StripLeadingTags(string title) => LeadingTagRegex().Replace(title, string.Empty);

    /// <summary>Minuscules, sans accents, mots séparés par toute ponctuation (« L'Été.2024 » → l, ete, 2024).</summary>
    internal static string[] Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return sb.ToString().Replace("œ", "oe", StringComparison.Ordinal).Replace("æ", "ae", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex(@"^\s*(?:[\[\(\{][^\]\)\}]*[\]\)\}]\s*[-._]*\s*)+")]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:s(?<s1>\d{1,2})(?:[ ._]?(?:-|à|a|to)[ ._]?s?(?<s2>\d{1,2}))?|(?:saison|season)[ ._-]?(?<s1>\d{1,2})(?:[ ._]?(?:-|à|a|to)[ ._]?(?<s2>\d{1,2}))?)(?![0-9])(?!e\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();
}
