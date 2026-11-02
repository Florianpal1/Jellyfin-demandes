using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;
using Jellyfin.Plugin.C411Torbox.Services.Import;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class ReleaseParserTests
{
    [Theory]
    [InlineData("The.Bear.S01E02.1080p.WEB-DL.mkv", 1, new[] { 2 })]
    [InlineData("the bear s1e2.mkv", 1, new[] { 2 })]
    [InlineData("Show.S02E03E04.720p.mkv", 2, new[] { 3, 4 })]
    [InlineData("Show.S02E03-E05.mkv", 2, new[] { 3, 4, 5 })]
    [InlineData("Show.S02E03-04.mkv", 2, new[] { 3, 4 })]
    [InlineData("Show - 3x07 - Titre.avi", 3, new[] { 7 })]
    [InlineData("Pack/Season 1/Show.S01E10.MULTi.2160p.x265.mkv", 1, new[] { 10 })]
    public void ParseEpisode_ReadsSeasonAndEpisodes(string name, int season, int[] episodes)
    {
        var n = ReleaseParser.ParseEpisode(name);

        Assert.NotNull(n);
        Assert.Equal(season, n!.Season);
        Assert.Equal(episodes, n.Episodes);
    }

    [Theory]
    [InlineData("Show.E05.mkv", 5)]
    [InlineData("05 - Le titre.mkv", 5)]
    public void ParseEpisode_WithoutSeason(string name, int episode)
    {
        var n = ReleaseParser.ParseEpisode(name);

        Assert.NotNull(n);
        Assert.Null(n!.Season);
        Assert.Equal(new[] { episode }, n.Episodes);
    }

    [Theory]
    [InlineData("Dune.2021.MULTi.1080p.x264.mkv")]
    [InlineData("Film.1920x1080.H264.mkv")]
    [InlineData("Show.S01E05-10bit.mkv")]
    public void ParseEpisode_DoesNotInventEpisodes(string name)
    {
        var n = ReleaseParser.ParseEpisode(name);

        Assert.True(n is null || n.Episodes.SequenceEqual(new[] { 5 }));
    }

    [Theory]
    [InlineData("Dune.2021.MULTi.1080p.BluRay.x264", "Bluray-1080p")]
    [InlineData("Dune.2021.2160p.UHD.BluRay.REMUX.HDR", "Remux-2160p")]
    [InlineData("The.Bear.S01.FRENCH.720p.WEB-DL.H264", "WEBDL-720p")]
    [InlineData("The.Bear.S01.1080p.WEBRip.x265", "WEBRip-1080p")]
    [InlineData("Show.S01E01.HDTV.x264", "HDTV")]
    [InlineData("Film.2010.MULTI.4K.HDR", "2160p")]
    [InlineData("Film.2010.DVDRip.XviD", "DVD")]
    [InlineData("Film.2010.MULTi.HDLight.1080p", "Bluray-1080p")]
    [InlineData("Film", "")]
    public void ParseQuality_UsesRadarrNames(string name, string expected)
        => Assert.Equal(expected, ReleaseParser.ParseQuality(name));

    [Theory]
    [InlineData("Film.FRENCH.srt", ".fr")]
    [InlineData("Subs/2_English.srt", ".en")]
    [InlineData("film.fr.forced.srt", ".fr.forced")]
    [InlineData("film.eng.sdh.srt", ".en.sdh")]
    [InlineData("film.srt", "")]
    public void SubtitleSuffix_KeepsLanguageForJellyfin(string name, string expected)
        => Assert.Equal(expected, ReleaseParser.SubtitleSuffix(name));

    [Fact]
    public void Samples_AndExtras_AreRecognized()
    {
        Assert.True(ReleaseParser.IsSample("Dune/Sample/dune-sample.mkv", 50L * 1024 * 1024));
        Assert.False(ReleaseParser.IsSample("Dune.mkv", 10L * 1024 * 1024 * 1024));
        Assert.True(ReleaseParser.IsInExtraFolder("Dune/Featurettes/Making of.mkv"));
        Assert.False(ReleaseParser.IsInExtraFolder("Dune/Dune.2021.mkv"));
        Assert.False(ReleaseParser.IsVideo("Dune/setup.exe"));
    }
}

public class LibraryNamingTests
{
    [Fact]
    public void Clean_RemovesForbiddenCharacters()
    {
        Assert.Equal("Mission - Impossible", LibraryNaming.Clean("Mission: Impossible"));
        Assert.Equal("What If", LibraryNaming.Clean("What If...?"));
        Assert.Equal("AC-DC Live", LibraryNaming.Clean("AC/DC  Live"));
        Assert.Equal("Sans titre", LibraryNaming.Clean("  "));
    }

    [Fact]
    public void Folders_FollowRadarrSonarrStandard()
    {
        Assert.Equal("Inception (2010) [tmdbid-27205]", LibraryNaming.MovieFolder("Inception", 2010, "27205"));
        Assert.Equal("Inception (2010) Bluray-1080p", LibraryNaming.MovieFileBase("Inception", 2010, "Bluray-1080p"));
        Assert.Equal("Season 01", LibraryNaming.SeasonFolder(1));
        Assert.Equal("Specials", LibraryNaming.SeasonFolder(0));
        Assert.Equal(
            "Breaking Bad - S01E01-02 - Pilote + Le chat WEBDL-1080p",
            LibraryNaming.EpisodeFileBase("Breaking Bad", 1, new[] { 1, 2 }, "Pilote + Le chat", "WEBDL-1080p"));
        Assert.Equal("Breaking Bad - S01E03", LibraryNaming.EpisodeFileBase("Breaking Bad", 1, new[] { 3 }, null, string.Empty));
    }
}

public class ImportPlannerTests
{
    private const long Mb = 1024L * 1024;

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lib");

    private static MediaRequest Movie() => new()
    {
        MediaKind = MediaKind.Movie,
        Scope = RequestScope.Movie,
        MediaTitle = "Dune",
        Year = 2021,
        TmdbId = "438631",
        TorrentTitle = "Dune.2021.MULTi.1080p.BluRay.x264",
    };

    private static MediaRequest Season(int season = 1) => new()
    {
        MediaKind = MediaKind.Series,
        Scope = RequestScope.Season,
        MediaTitle = "The Bear",
        Year = 2022,
        TmdbId = "136315",
        Season = season,
        TorrentTitle = "The.Bear.S01.MULTi.1080p.WEB-DL",
    };

    [Fact]
    public void Movie_TakesMainVideo_SkipsSampleAndExtras_KeepsSubtitles()
    {
        var files = new[]
        {
            new TorboxFile(1, "Dune.2021/Dune.2021.MULTi.1080p.BluRay.x264.mkv", 9000 * Mb),
            new TorboxFile(2, "Dune.2021/Sample/sample.mkv", 40 * Mb),
            new TorboxFile(3, "Dune.2021/Featurettes/Making of.mkv", 800 * Mb),
            new TorboxFile(4, "Dune.2021/Dune.FRENCH.srt", 1),
            new TorboxFile(5, "Dune.2021/Dune.2021.nfo", 1),
        };
        var folder = Path.Combine(Root, LibraryNaming.MovieFolder("Dune", 2021, "438631"));

        var plan = ImportPlanner.Plan(Movie(), files, new ImportTarget { Folder = folder });

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv"), plan.Items[0].Destination);
        Assert.Equal(Path.Combine(folder, "Dune (2021) Bluray-1080p.fr.srt"), plan.Items[1].Destination);
        Assert.Equal(9000 * Mb + 1, plan.TotalBytes);
    }

    [Fact]
    public void Movie_WithoutVideo_Fails()
    {
        var files = new[] { new TorboxFile(1, "Dune/readme.txt", 1) };

        Assert.Throws<ImportException>(() => ImportPlanner.Plan(Movie(), files, new ImportTarget { Folder = Root }));
    }

    [Fact]
    public void SeasonPack_NamesEpisodes_SkipsOtherSeasonsAndExistingEpisodes()
    {
        var files = new[]
        {
            new TorboxFile(1, "The.Bear.S01/The.Bear.S01E01.1080p.WEB-DL.mkv", 1000 * Mb),
            new TorboxFile(2, "The.Bear.S01/The.Bear.S01E02.1080p.WEB-DL.mkv", 1000 * Mb),
            new TorboxFile(3, "The.Bear.S01/The.Bear.S01E03.1080p.WEB-DL.mkv", 1000 * Mb),
            new TorboxFile(4, "The.Bear.S01/The.Bear.S02E01.1080p.WEB-DL.mkv", 1000 * Mb),
            new TorboxFile(5, "The.Bear.S01/Subs/The.Bear.S01E02/3_French.srt", 1),
        };
        var request = Season();
        var series = Path.Combine(Root, "The Bear (2022) [tmdbid-136315]");

        Assert.Equal(new[] { (1, 1), (1, 2), (1, 3) }, ImportPlanner.FindEpisodes(request, files));

        var plan = ImportPlanner.Plan(request, files, new ImportTarget
        {
            Folder = series,
            EpisodeTitles = new Dictionary<(int, int), string> { [(1, 2)] = "Mains: propres" },
            EpisodeExists = (s, e) => s == 1 && e == 3,
        });

        var season = Path.Combine(series, "Season 01");
        Assert.Equal(
            new[]
            {
                Path.Combine(season, "The Bear - S01E01 WEBDL-1080p.mkv"),
                Path.Combine(season, "The Bear - S01E02 - Mains - propres WEBDL-1080p.mkv"),
                Path.Combine(season, "The Bear - S01E02 - Mains - propres WEBDL-1080p.fr.srt"),
            },
            plan.Items.Select(i => i.Destination));
        Assert.Contains(plan.Skipped, s => s.Contains("S02E01", StringComparison.Ordinal) && s.Contains("autre saison", StringComparison.Ordinal));
        Assert.Contains(plan.Skipped, s => s.Contains("S01E03", StringComparison.Ordinal) && s.Contains("déjà", StringComparison.Ordinal));
    }

    [Fact]
    public void Episode_PicksTheRequestedFile()
    {
        var request = Season();
        request.Scope = RequestScope.Episode;
        request.Episode = 2;
        var files = new[]
        {
            new TorboxFile(1, "Pack/The.Bear.S01E01.mkv", 1000 * Mb),
            new TorboxFile(2, "Pack/The.Bear.S01E02.mkv", 1000 * Mb),
        };

        var plan = ImportPlanner.Plan(request, files, new ImportTarget { Folder = Root });

        Assert.Single(plan.Items);
        Assert.Equal(2, plan.Items[0].File.Id);
        Assert.EndsWith("The Bear - S01E02 WEBDL-1080p.mkv", plan.Items[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public void Episode_SingleUnnamedVideo_IsTheRequestedEpisode()
    {
        var request = Season();
        request.Scope = RequestScope.Episode;
        request.Episode = 4;
        var files = new[] { new TorboxFile(1, "the-bear-quatre.mkv", 1000 * Mb) };

        var plan = ImportPlanner.Plan(request, files, new ImportTarget { Folder = Root });

        Assert.EndsWith("The Bear - S01E04 WEBDL-1080p.mkv", plan.Items[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonPack_AllEpisodesAlreadyPresent_Fails()
    {
        var files = new[] { new TorboxFile(1, "The.Bear.S01E01.mkv", 1000 * Mb) };

        var ex = Assert.Throws<ImportException>(() => ImportPlanner.Plan(Season(), files, new ImportTarget
        {
            Folder = Root,
            EpisodeExists = (_, _) => true,
        }));
        Assert.Contains("déjà", ex.Message, StringComparison.Ordinal);
    }
}
