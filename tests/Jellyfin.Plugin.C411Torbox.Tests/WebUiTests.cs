using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;
using Jint;
using Jint.Native;

namespace Jellyfin.Plugin.C411Torbox.Tests;

/// <summary>
/// Interface web embarquée dans le plugin, exécutée avec le moteur JavaScript Jint (sans navigateur) :
/// syntaxe des scripts, fonctions pures de app.js et cohérence avec le serveur.
/// </summary>
public partial class WebUiTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static string Resource(string suffix)
    {
        var name = PluginAssembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = PluginAssembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>app.js chargé sans DOM (seule l'adresse de la page est simulée) : il expose ses fonctions pures dans « C411Ui ».</summary>
    private static Engine LoadApp()
    {
        var engine = new Engine(o => o.Culture(System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        engine.Execute("var window = { location: { pathname: '/demandes/ui' } };");
        engine.Execute(Resource("Web.app.js"));
        return engine;
    }

    private static JsValue Call(Engine engine, string function, params object?[] args)
        => engine.Invoke(engine.Evaluate("C411Ui." + function), args);

    private static JsValue Request(Engine engine, string json) => engine.Evaluate("(" + json + ")");

    [GeneratedRegex(@"<script[^>]*>(?<code>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [Theory]
    [InlineData("Configuration.configPage.html")]
    [InlineData("Configuration.requestsPage.html")]
    [InlineData("Web.app.html")]
    public void InlineScripts_AreValidJavaScript(string page)
    {
        var scripts = InlineScript().Matches(Resource(page));
        Assert.NotEmpty(scripts);
        foreach (Match m in scripts)
        {
            // Lève une exception en cas d'erreur de syntaxe.
            Engine.PrepareScript(m.Groups["code"].Value);
        }
    }

    [Fact]
    public void App_LoadsWithoutBrowser_AndExposesHelpers()
    {
        var engine = LoadApp();
        Assert.True(engine.Evaluate("typeof C411Ui.describe === 'function'").AsBoolean());
    }

    [Fact]
    public void StatusLabels_CoverEveryServerStatus()
    {
        var engine = LoadApp();
        foreach (var status in Enum.GetNames<RequestStatus>())
        {
            Assert.True(engine.Evaluate($"typeof C411Ui.STATUS_LABELS['{status}'] === 'string'").AsBoolean(), "Libellé manquant : " + status);
        }
    }

    [Fact]
    public void Describe_And_FormatSize()
    {
        var engine = LoadApp();
        Assert.Equal("Dune (2021)", Call(engine, "describe", Request(engine, "{ scope: 'Movie', mediaTitle: 'Dune', year: 2021 }")).AsString());
        Assert.Equal("The Bear S01E02", Call(engine, "describe", Request(engine, "{ scope: 'Episode', mediaTitle: 'The Bear', season: 1, episode: 2 }")).AsString());
        Assert.Equal("The Bear – saison 3", Call(engine, "describe", Request(engine, "{ scope: 'Season', mediaTitle: 'The Bear', season: 3 }")).AsString());
        Assert.Equal("—", Call(engine, "formatSize", 0).AsString());
        Assert.EndsWith(" Go", Call(engine, "formatSize", 4.5 * 1024 * 1024 * 1024).AsString(), StringComparison.Ordinal);
        Assert.EndsWith(" Mo", Call(engine, "formatSize", 300 * 1024 * 1024).AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SafeImage_OnlyAcceptsHttps()
    {
        var engine = LoadApp();
        Assert.Equal("https://image.tmdb.org/a.jpg", Call(engine, "safeImage", "https://image.tmdb.org/a.jpg").AsString());
        Assert.True(Call(engine, "safeImage", "javascript:alert(1)").IsNull());
        Assert.True(Call(engine, "safeImage", "http://image.tmdb.org/a.jpg").IsNull());
    }

    [Theory]
    [InlineData("Pending", true, true, false)]
    [InlineData("Failed", true, true, false)]
    [InlineData("AutoApproved", false, false, false)]
    [InlineData("SentToTorbox", false, true, true)]
    [InlineData("Importing", false, false, false)]
    [InlineData("Imported", false, true, true)]
    [InlineData("Available", false, false, false)]
    [InlineData("Cancelled", false, false, false)]
    public void Actions_MatchServerRules(string status, bool userCancel, bool adminCancel, bool markAvailable)
    {
        var engine = LoadApp();
        var r = Request(engine, $"{{ status: '{status}' }}");
        Assert.Equal(userCancel, Call(engine, "canUserCancel", r).AsBoolean());
        Assert.Equal(adminCancel, Call(engine, "canAdminCancel", r).AsBoolean());
        Assert.Equal(markAvailable, Call(engine, "canMarkAvailable", r).AsBoolean());
    }

    [Fact]
    public void EpisodeLabel_ShowsTitleAndLibraryState()
    {
        var engine = LoadApp();
        Assert.Equal("E02 · Mains propres", Call(engine, "episodeLabel", Request(engine, "{ episode: 2, title: 'Mains propres', inLibrary: false }")).AsString());
        Assert.Equal("E10 (déjà dans Jellyfin)", Call(engine, "episodeLabel", Request(engine, "{ episode: 10, title: null, inLibrary: true }")).AsString());
    }

    [Fact]
    public void StaleDays_And_RetryText()
    {
        var engine = LoadApp();
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stale = Request(engine, "{ status: 'DownloadingOnTorbox', createdAt: '2026-11-05T12:00:00Z', staleNotifiedAt: '2026-11-09T04:00:00Z' }");
        Assert.Equal(5, Call(engine, "staleDays", stale, now).AsNumber());
        Assert.True(Call(engine, "staleDays", Request(engine, "{ status: 'Available', createdAt: '2026-11-05T12:00:00Z', staleNotifiedAt: '2026-11-09T04:00:00Z' }"), now).IsNull());

        var waiting = Request(engine, "{ status: 'CompletedOnTorbox', retryImportAfter: '2026-11-10T12:30:00Z' }");
        Assert.StartsWith("Nouvel essai de rangement à", Call(engine, "retryText", waiting, now).AsString(), StringComparison.Ordinal);
        Assert.True(Call(engine, "retryText", Request(engine, "{ status: 'Importing', retryImportAfter: '2026-11-10T12:30:00Z' }"), now).IsNull());
    }

    [Fact]
    public void ConfigPage_FieldsMatchPluginConfiguration()
    {
        var html = Resource("Configuration.configPage.html");
        var properties = typeof(PluginConfiguration).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var list in new[] { "textFields", "checkFields", "numberFields" })
        {
            var match = Regex.Match(html, "var " + list + @" = \[(?<ids>[^\]]*)\]");
            Assert.True(match.Success, list);
            foreach (Match id in Regex.Matches(match.Groups["ids"].Value, "'(?<id>[^']+)'"))
            {
                var name = id.Groups["id"].Value;
                Assert.True(properties.Contains(name), $"{name} n'est pas un réglage du plugin");
                Assert.Contains("id=\"" + name + "\"", html, StringComparison.Ordinal);
            }
        }
    }
}
