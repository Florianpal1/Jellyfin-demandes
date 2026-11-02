using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Présence d'un média dans la bibliothèque Jellyfin (par identifiants TMDB/IMDb/TVDB).</summary>
public interface ILibraryLookup
{
    bool IsMovieInLibrary(IReadOnlyDictionary<string, string> providerIds);

    bool IsSeriesInLibrary(IReadOnlyDictionary<string, string> providerIds);

    /// <summary>Nombre d'épisodes présents pour une saison (ou un épisode précis si <paramref name="episode"/> est renseigné).</summary>
    int CountEpisodes(IReadOnlyDictionary<string, string> providerIds, int season, int? episode);

    /// <summary>Dossier d'une série déjà présente dans la bibliothèque (pour y ranger les nouveaux épisodes), sinon null.</summary>
    string? GetSeriesFolder(IReadOnlyDictionary<string, string> providerIds);

    /// <summary>Dossier existant d'une saison de la série, sinon null.</summary>
    string? GetSeasonFolder(IReadOnlyDictionary<string, string> providerIds, int season);

    /// <summary>Dossiers des bibliothèques Jellyfin de type Films ou Séries.</summary>
    IReadOnlyList<string> GetLibraryFolders(Models.MediaKind kind);
}

internal sealed class LibraryLookup : ILibraryLookup
{
    internal static readonly string[] SupportedProviders = ["Tmdb", "Imdb", "Tvdb"];

    private readonly ILibraryManager _libraryManager;

    public LibraryLookup(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public bool IsMovieInLibrary(IReadOnlyDictionary<string, string> providerIds)
        => FindItems(BaseItemKind.Movie, providerIds, 1).Count > 0;

    public bool IsSeriesInLibrary(IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var series in FindItems(BaseItemKind.Series, providerIds, 5))
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [series.Id],
                Recursive = true,
                IsVirtualItem = false,
                Limit = 1,
            };
            if (_libraryManager.GetItemList(query).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public int CountEpisodes(IReadOnlyDictionary<string, string> providerIds, int season, int? episode)
    {
        int count = 0;
        foreach (var series in FindItems(BaseItemKind.Series, providerIds, 5))
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [series.Id],
                ParentIndexNumber = season,
                IndexNumber = episode,
                Recursive = true,
                IsVirtualItem = false,
            };
            count += _libraryManager.GetItemList(query).Count;
        }

        return count;
    }

    public string? GetSeriesFolder(IReadOnlyDictionary<string, string> providerIds)
        => FindItems(BaseItemKind.Series, providerIds, 5)
            .Select(s => s.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));

    public string? GetSeasonFolder(IReadOnlyDictionary<string, string> providerIds, int season)
    {
        foreach (var series in FindItems(BaseItemKind.Series, providerIds, 5))
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                AncestorIds = [series.Id],
                IndexNumber = season,
                Recursive = true,
                IsVirtualItem = false,
            };

            // Une saison sans dossier propre (épisodes à la racine de la série) a le chemin de la série : ignorée.
            var path = _libraryManager.GetItemList(query)
                .Select(s => s.Path)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p)
                    && !string.Equals(p, series.Path, StringComparison.Ordinal)
                    && Directory.Exists(p));
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetLibraryFolders(Models.MediaKind kind)
    {
        var type = kind == Models.MediaKind.Movie ? CollectionTypeOptions.movies : CollectionTypeOptions.tvshows;
        return _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType == type)
            .SelectMany(f => f.Locations ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<BaseItem> FindItems(BaseItemKind kind, IReadOnlyDictionary<string, string> providerIds, int limit)
    {
        var ids = FilterIds(providerIds);

        // Sans identifiant, HasAnyProviderId ne filtrerait rien : on ne renvoie surtout pas toute la bibliothèque.
        if (ids.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            HasAnyProviderId = ids,
            Recursive = true,
            IsVirtualItem = false,
            Limit = limit,
        };
        return _libraryManager.GetItemList(query);
    }

    internal static Dictionary<string, string> FilterIds(IReadOnlyDictionary<string, string> providerIds)
        => providerIds
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value)
                && SupportedProviders.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(
                kv => SupportedProviders.First(p => p.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)),
                kv => kv.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
}
