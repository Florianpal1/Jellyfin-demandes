using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class ReleaseMatcherTests
{
    private static bool Movie(string title, string name = "Dune", int? year = 2021, string? tmdbId = null, params string[] aliases)
        => new ReleaseMatcher(new TorrentSearchRequest
        {
            Kind = MediaKind.Movie,
            Name = name,
            Year = year,
            ProviderIds = new() { ["Tmdb"] = "438631", ["Imdb"] = "tt1160419" },
            AlternateNames = aliases,
        }).IsRelevant(new TorrentResult { Title = title, TmdbId = tmdbId });

    private static bool Series(string title, int season, int? episode, string name = "The Bear")
        => new ReleaseMatcher(new TorrentSearchRequest { Kind = MediaKind.Series, Name = name, Season = season, Episode = episode })
            .IsRelevant(new TorrentResult { Title = title });

    [Theory]
    [InlineData("Dune.2021.MULTi.1080p.BluRay.x264")]
    [InlineData("Dune (2021) MULTi VFF 2160p")]
    [InlineData("[Team] Dune.2022.1080p")] // année ± 1 (sortie en salle / sur support)
    [InlineData("DUNE.MULTi.1080p")] // sans année
    public void Movie_SameTitleAndYear_IsKept(string title) => Assert.True(Movie(title));

    [Theory]
    [InlineData("Oppenheimer.2023.MULTi.2160p")]
    [InlineData("Dune.1984.MULTi.1080p")] // autre film du même nom
    [InlineData("Dune.Part.Two.2024.MULTi.1080p")]
    [InlineData("Jodorowskys.Dune.2013.1080p")]
    public void Movie_UnrelatedRelease_IsDropped(string title) => Assert.False(Movie(title));

    [Fact]
    public void Movie_IgnoresAccentsAndPunctuation()
    {
        Assert.True(Movie("L.Ete.Dernier.2023.FRENCH.1080p", "L'Été dernier", 2023));
        Assert.True(Movie("Spider-Man.No.Way.Home.2021.MULTi", "Spider-Man : No Way Home", 2021));
    }

    [Fact]
    public void Movie_MatchesAlternateTitle()
    {
        Assert.True(Movie("The.Godfather.1972.MULTi.1080p", "Le Parrain", 1972, null, "The Godfather"));
        Assert.False(Movie("The.Godfather.1972.MULTi.1080p", "Le Parrain", 1972));
    }

    [Fact]
    public void Movie_IndexerIdWins()
    {
        Assert.True(Movie("Some.Weird.Name.1080p", tmdbId: "438631"));
        Assert.False(Movie("Dune.2021.1080p", tmdbId: "693134"));
    }

    [Theory]
    [InlineData("The.Bear.S02E03.MULTi.1080p", true)]
    [InlineData("The Bear S02E01-E04 1080p", true)]
    [InlineData("The.Bear.S02.MULTi.1080p", true)] // pack de la saison
    [InlineData("The.Bear.Integrale.MULTi.1080p", true)]
    [InlineData("The.Bear.S02E05.MULTi.1080p", false)]
    [InlineData("The.Bear.S01E03.MULTi.1080p", false)]
    [InlineData("The.Bear.S03.MULTi.1080p", false)]
    [InlineData("Shogun.S02E03.1080p", false)]
    public void Series_Episode(string title, bool expected) => Assert.Equal(expected, Series(title, 2, 3));

    [Theory]
    [InlineData("The.Bear.S02.MULTi.1080p", true)]
    [InlineData("The.Bear.Saison.2.FRENCH.1080p", true)]
    [InlineData("The.Bear.S01-S03.MULTi.1080p", true)]
    [InlineData("The.Bear.S02E03.MULTi.1080p", false)] // épisode isolé : pas un pack
    [InlineData("The.Bear.S03.MULTi.1080p", false)]
    public void Series_SeasonPack(string title, bool expected) => Assert.Equal(expected, Series(title, 2, null));

    [Theory]
    [InlineData("Frank Herbert - Dune [EPUB]", true)]
    [InlineData("Dune.Frank.Herbert.2021.EPUB", true)]
    [InlineData("Fondation - Isaac Asimov [EPUB]", false)]
    public void Book_TitleAnywhereInName(string title, bool expected)
        => Assert.Equal(expected, new ReleaseMatcher(new TorrentSearchRequest { Kind = MediaKind.Book, Name = "Dune" })
            .IsRelevant(new TorrentResult { Title = title }));
}
