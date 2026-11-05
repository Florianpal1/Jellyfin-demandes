using System.Reflection;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class MetadataSearchServiceTests
{
    private static RemoteSearchResult Result(string name, int? year, params (string Key, string Value)[] ids)
        => new() { Name = name, ProductionYear = year, ProviderIds = ids.ToDictionary(i => i.Key, i => i.Value) };

    private static (MetadataSearchService Service, FakeProviderManager Providers) Create(Func<string?, IEnumerable<RemoteSearchResult>> search)
    {
        var providers = FakeProviderManager.Create(search);
        var service = new MetadataSearchService(providers, new FakeLibrary(), new MediaResultCache());
        return (service, (FakeProviderManager)(object)providers);
    }

    [Fact]
    public async Task Movies_AreSearchedOnTmdbOnly()
    {
        // OMDb renverrait le même film sous son titre anglais avec le seul identifiant Imdb : il ne doit pas être interrogé.
        var (service, providers) = Create(name => name == "TheMovieDb"
            ? [Result("Le Parrain", 1972, ("Tmdb", "238"))]
            : [Result("The Godfather", 1972, ("Imdb", "tt0068646"))]);

        var results = await service.SearchAsync("parrain", MediaKind.Movie, null, CancellationToken.None);

        Assert.Equal("Le Parrain", Assert.Single(results).Name);
        Assert.Equal(["TheMovieDb"], providers.Queries.Select(q => q.Provider));
    }

    [Fact]
    public async Task OtherProviders_AreUsedWhenTmdbFindsNothing()
    {
        var (service, providers) = Create(name => name == "TheMovieDb"
            ? []
            : [Result("Obscure", 1950, ("Imdb", "tt1"))]);

        var results = await service.SearchAsync("obscure", MediaKind.Movie, null, CancellationToken.None);

        Assert.Equal("Obscure", Assert.Single(results).Name);
        Assert.Equal(["TheMovieDb", null], providers.Queries.Select(q => q.Provider));
    }

    [Fact]
    public async Task Year_IsSentToProvidersAndFiltersResults()
    {
        var (service, providers) = Create(_ =>
            [Result("Dune", 1984, ("Tmdb", "841")), Result("Dune", 2021, ("Tmdb", "438631")), Result("Dune", null, ("Tmdb", "1"))]);

        var results = await service.SearchAsync("dune", MediaKind.Movie, 2021, CancellationToken.None);

        Assert.Equal(2021, Assert.Single(results).Year);
        Assert.Equal(2021, providers.Queries[0].Year);
    }

    [Fact]
    public async Task Year_FallsBackToOtherProvidersWhenNoTmdbResultMatches()
    {
        var (service, providers) = Create(name => name == "TheMovieDb"
            ? [Result("Dune", 1984, ("Tmdb", "841"))]
            : [Result("Dune", 2000, ("Imdb", "tt2"))]);

        var results = await service.SearchAsync("dune", MediaKind.Movie, 2000, CancellationToken.None);

        Assert.Equal(2000, Assert.Single(results).Year);
        Assert.Equal(2, providers.Queries.Count);
    }

    [Theory]
    [InlineData("tt0068646", MediaKind.Movie, "Imdb", "tt0068646")]
    [InlineData("https://www.imdb.com/title/tt0068646/?ref_=nv_sr_1", MediaKind.Movie, "Imdb", "tt0068646")]
    [InlineData("https://www.themoviedb.org/movie/238-the-godfather", MediaKind.Movie, "Tmdb", "238")]
    [InlineData("https://www.themoviedb.org/fr-FR/tv/1399", MediaKind.Series, "Tmdb", "1399")]
    [InlineData("TMDB:238", MediaKind.Movie, "Tmdb", "238")]
    [InlineData("tvdb : 81189", MediaKind.Movie, "Tvdb", "81189")]
    [InlineData("imdb:TT0068646", MediaKind.Movie, "Imdb", "tt0068646")]
    public void ParseIdQuery_RecognizesIdsAndLinks(string query, MediaKind expectedKind, string provider, string value)
    {
        Assert.Equal((expectedKind, provider, value), MetadataSearchService.ParseIdQuery(query, MediaKind.Movie));
    }

    [Theory]
    [InlineData("1917")]
    [InlineData("300")]
    [InlineData("Le Parrain")]
    [InlineData("tmdb:tt0068646")]
    [InlineData("imdb:238")]
    [InlineData("film tt0068646")]
    public void ParseIdQuery_LeavesTitlesAlone(string query)
    {
        Assert.Null(MetadataSearchService.ParseIdQuery(query, MediaKind.Movie));
    }

    [Fact]
    public async Task IdSearch_LooksUpTheIdAndIgnoresYear()
    {
        var (service, providers) = Create(_ => [Result("Game of Thrones", 2011, ("Tmdb", "1399"))]);

        var results = await service.SearchAsync("https://www.themoviedb.org/tv/1399", MediaKind.Movie, 1990, CancellationToken.None);

        // Le lien désigne une série : la fiche est une série, même si « Film » était sélectionné.
        Assert.Equal(MediaKind.Series, Assert.Single(results).Kind);
        var info = Assert.Single(providers.Queries).Info;
        Assert.IsType<SeriesInfo>(info);
        Assert.Equal("1399", info.ProviderIds["Tmdb"]);
        Assert.Null(info.Year);
        Assert.Equal(string.Empty, info.Name);
    }
}

/// <summary>
/// <see cref="IProviderManager"/> factice (interface très large) : seule la recherche répond.
/// </summary>
public class FakeProviderManager : DispatchProxy
{
    private Func<string?, IEnumerable<RemoteSearchResult>> _search = _ => [];

    /// <summary>Recherches reçues : fournisseur demandé (null = tous) et critères.</summary>
    public List<(string? Provider, int? Year, ItemLookupInfo Info)> Queries { get; } = new();

    internal static IProviderManager Create(Func<string?, IEnumerable<RemoteSearchResult>> search)
    {
        var proxy = DispatchProxy.Create<IProviderManager, FakeProviderManager>();
        ((FakeProviderManager)(object)proxy)._search = search;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IProviderManager.GetRemoteSearchResults))
        {
            throw new NotSupportedException(targetMethod?.Name);
        }

        var query = args![0]!;
        var provider = (string?)query.GetType().GetProperty("SearchProviderName")!.GetValue(query);
        var info = (ItemLookupInfo)query.GetType().GetProperty("SearchInfo")!.GetValue(query)!;
        lock (Queries)
        {
            Queries.Add((provider, info.Year, info));
        }

        return Task.FromResult<IEnumerable<RemoteSearchResult>>(_search(provider).ToList());
    }
}
