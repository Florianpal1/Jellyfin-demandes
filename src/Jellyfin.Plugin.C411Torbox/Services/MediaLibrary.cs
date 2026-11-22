using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.C411Torbox.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Un média de la bibliothèque à remplacer : de quoi chercher ses torrents et la liste de ses fichiers actuels.</summary>
public sealed class LibraryMedia
{
    public Guid Id { get; init; }

    public LibraryItemType Type { get; init; }

    /// <summary>Titre du film, de la série (saisons et épisodes) ou du livre.</summary>
    public string Name { get; init; } = string.Empty;

    public int? Year { get; init; }

    /// <summary>Identifiants du film, de la série ou du livre (TMDB, IMDb, TVDB, Google Books…).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    public int? Season { get; init; }

    public int? Episode { get; init; }

    /// <summary>Fichiers actuels, supprimés une fois les nouveaux rangés.</summary>
    public IReadOnlyList<ReplacedFile> Files { get; init; } = Array.Empty<ReplacedFile>();

    public MediaKind Kind => Type switch
    {
        LibraryItemType.Movie => MediaKind.Movie,
        LibraryItemType.Book => MediaKind.Book,
        _ => MediaKind.Series,
    };

    /// <summary>Les séries entières ne se remplacent pas (saison par saison ou épisode par épisode).</summary>
    public bool CanReplace => Type != LibraryItemType.Series
        && (Type is not (LibraryItemType.Season or LibraryItemType.Episode) || Season.HasValue)
        && (Type != LibraryItemType.Episode || Episode.HasValue);

    /// <summary>Recherche de torrents pour ce média, même s'il est déjà dans la bibliothèque.</summary>
    public TorrentSearchRequest ToSearch() => new()
    {
        Kind = Kind,
        Name = Name,
        Year = Year,
        ProviderIds = new Dictionary<string, string>(ProviderIds, StringComparer.OrdinalIgnoreCase),
        Season = Kind == MediaKind.Series ? Season : null,
        Episode = Type == LibraryItemType.Episode ? Episode : null,
        IgnoreLibrary = true,
    };
}

/// <summary>Consultation et suppression des médias de la bibliothèque Jellyfin (page « Médias » des administrateurs).</summary>
public interface IMediaLibrary
{
    /// <summary>Films, séries ou livres, page par page.</summary>
    LibraryPage Browse(LibraryItemType type, string? search, LibrarySort sort, int start, int limit);

    /// <summary>Saisons d'une série et leurs épisodes présents (null si ce n'est pas une série).</summary>
    IReadOnlyList<LibraryItem>? GetSeasons(Guid seriesId);

    /// <summary>Média à remplacer (null s'il n'existe pas ou n'est ni film, ni série, ni saison, ni épisode, ni livre).</summary>
    LibraryMedia? Get(Guid id);

    /// <summary>Supprime le média de Jellyfin et ses fichiers du disque. Renvoie son nom, null s'il n'existe pas.</summary>
    string? Delete(Guid id);
}

internal sealed class MediaLibrary : IMediaLibrary
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaLibrary> _logger;

    public MediaLibrary(ILibraryManager libraryManager, ILogger<MediaLibrary> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public LibraryPage Browse(LibraryItemType type, string? search, LibrarySort sort, int start, int limit)
    {
        var kind = type switch
        {
            LibraryItemType.Movie => BaseItemKind.Movie,
            LibraryItemType.Book => BaseItemKind.Book,
            _ => BaseItemKind.Series,
        };
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            IsVirtualItem = false,
            StartIndex = start,
            Limit = limit,
            OrderBy = sort == LibrarySort.Recent
                ? [(ItemSortBy.DateCreated, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)]
                : [(ItemSortBy.SortName, SortOrder.Ascending)],
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.SearchTerm = search.Trim();
        }

        var result = _libraryManager.GetItemsResult(query);
        return new LibraryPage
        {
            Items = result.Items.Select(ToItem).ToList(),
            Total = result.TotalRecordCount,
            Start = start,
        };
    }

    public IReadOnlyList<LibraryItem>? GetSeasons(Guid seriesId)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return null;
        }

        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Season],
            AncestorIds = [series.Id],
            Recursive = true,
        }).OfType<Season>().ToList();
        var episodes = EpisodesOf(series.Id, null);

        return episodes
            .GroupBy(e => e.ParentIndexNumber ?? -1)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var number = g.Key < 0 ? (int?)null : g.Key;
                var season = seasons.FirstOrDefault(s => s.IndexNumber == number)
                    ?? (g.First().SeasonId is var sid && sid != Guid.Empty ? _libraryManager.GetItemById(sid) as Season : null);
                var items = g.OrderBy(e => e.IndexNumber ?? int.MaxValue).Select(ToItem).ToList();
                return new LibraryItem
                {
                    Id = (season?.Id ?? Guid.Empty).ToString("N"),
                    Type = LibraryItemType.Season,
                    Name = season?.Name ?? (number is { } n ? "Saison " + n : "Saison inconnue"),
                    Year = series.ProductionYear,
                    Season = number,
                    Path = season?.Path,
                    SizeBytes = items.Sum(i => i.SizeBytes ?? 0) is var total && total > 0 ? total : null,
                    DateAdded = items.Max(i => i.DateAdded),
                    HasImage = season?.HasImage(ImageType.Primary) ?? false,
                    Episodes = items,
                };
            })
            .ToList();
    }

    public LibraryMedia? Get(Guid id)
    {
        switch (_libraryManager.GetItemById(id))
        {
            case MediaBrowser.Controller.Entities.Movies.Movie movie:
                return new LibraryMedia
                {
                    Id = movie.Id,
                    Type = LibraryItemType.Movie,
                    Name = movie.Name,
                    Year = movie.ProductionYear,
                    ProviderIds = movie.ProviderIds,
                    Files = FilesOf([movie]),
                };
            case Book book:
                return new LibraryMedia
                {
                    Id = book.Id,
                    Type = LibraryItemType.Book,
                    Name = book.Name,
                    Year = book.ProductionYear,
                    ProviderIds = book.ProviderIds,
                    Files = FilesOf([book]),
                };
            case Episode episode when episode.Series is { } series:
                return new LibraryMedia
                {
                    Id = episode.Id,
                    Type = LibraryItemType.Episode,
                    Name = series.Name,
                    Year = series.ProductionYear,
                    ProviderIds = series.ProviderIds,
                    Season = episode.ParentIndexNumber,
                    Episode = episode.IndexNumber,
                    Files = FilesOf([episode]),
                };
            case Season season when season.Series is { } series:
                return new LibraryMedia
                {
                    Id = season.Id,
                    Type = LibraryItemType.Season,
                    Name = series.Name,
                    Year = series.ProductionYear,
                    ProviderIds = series.ProviderIds,
                    Season = season.IndexNumber,
                    Files = season.IndexNumber is { } n ? FilesOf(EpisodesOf(series.Id, n)) : Array.Empty<ReplacedFile>(),
                };
            case Series series:
                return new LibraryMedia
                {
                    Id = series.Id,
                    Type = LibraryItemType.Series,
                    Name = series.Name,
                    Year = series.ProductionYear,
                    ProviderIds = series.ProviderIds,
                };
            default:
                return null;
        }
    }

    public string? Delete(Guid id)
    {
        var item = _libraryManager.GetItemById(id);
        switch (item)
        {
            case null:
                return null;
            case Season season:
                // Une saison sans dossier propre a le chemin de la série : la supprimer d'un bloc effacerait toute la série.
                // On supprime donc ses épisodes un par un, puis son dossier s'il est vide.
                var label = (season.Series?.Name is { } name ? name + " – " : string.Empty) + season.Name;
                var seriesPath = season.Series?.Path;
                foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [season.Id],
                    Recursive = true,
                }))
                {
                    _libraryManager.DeleteItem(episode, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                if (!string.IsNullOrEmpty(season.Path) && !string.Equals(season.Path, seriesPath, StringComparison.Ordinal)
                    && Directory.Exists(season.Path) && !Directory.EnumerateFileSystemEntries(season.Path).Any())
                {
                    Directory.Delete(season.Path);
                }

                _libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false }, true);
                return label;
            case MediaBrowser.Controller.Entities.Movies.Movie or Series or Episode or Book:
                var description = item is Episode ep ? (ep.SeriesName + " – " + ep.Name) : item.Name;
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                return description;
            default:
                throw new InvalidOperationException("Seuls les films, séries, saisons, épisodes et livres peuvent être supprimés ici.");
        }
    }

    /// <summary>Définition affichée d'après la largeur de l'image (les films en 2,39:1 ont une hauteur réduite).</summary>
    internal static string? Resolution(int width, int height)
    {
        if (width <= 0 && height <= 0)
        {
            return null;
        }

        return width >= 3200 || height >= 1800 ? "2160p"
            : width >= 1800 || height >= 1000 ? "1080p"
            : width >= 1200 || height >= 700 ? "720p"
            : "SD";
    }

    private List<Episode> EpisodesOf(Guid seriesId, int? season)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            ParentIndexNumber = season,
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>().ToList();

    /// <summary>Fichiers vidéo ou livres présents sur le disque (pas les dossiers DVD / Blu-ray).</summary>
    private List<ReplacedFile> FilesOf(IEnumerable<BaseItem> items)
    {
        var files = new List<ReplacedFile>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                _logger.LogDebug("{Name} : pas de fichier unique à remplacer ({Path})", item.Name, item.Path);
                continue;
            }

            files.Add(item is Episode e
                ? new ReplacedFile(item.Path, e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd)
                : new ReplacedFile(item.Path));
        }

        return files;
    }

    private static LibraryItem ToItem(BaseItem item) => new()
    {
        Id = item.Id.ToString("N"),
        Type = item switch
        {
            MediaBrowser.Controller.Entities.Movies.Movie => LibraryItemType.Movie,
            Series => LibraryItemType.Series,
            Season => LibraryItemType.Season,
            Episode => LibraryItemType.Episode,
            _ => LibraryItemType.Book,
        },
        Name = item.Name,
        Year = item.ProductionYear,
        Season = item switch
        {
            Episode => item.ParentIndexNumber,
            Season => item.IndexNumber,
            _ => null,
        },
        Episode = item is Episode ? item.IndexNumber : null,
        EpisodeEnd = item is Episode { IndexNumberEnd: { } end } ? end : null,
        Path = item.Path,
        SizeBytes = item.Size is > 0 ? item.Size : null,
        Resolution = Resolution(item.Width, item.Height),
        DateAdded = item.DateCreated,
        HasImage = item.HasImage(ImageType.Primary),
    };
}
