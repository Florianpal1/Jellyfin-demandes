using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class NetworkErrorsTests
{
    [Fact]
    public void SslError_ExplainsHowToFix()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid: UntrustedRoot"));

        var message = NetworkErrors.Describe(ex);

        Assert.Contains("certificat non valide", message, StringComparison.Ordinal);
        Assert.Contains("UntrustedRoot", message, StringComparison.Ordinal);
        Assert.Contains("http://", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionRefused_IsTranslated()
        => Assert.StartsWith(
            "connexion refusée",
            NetworkErrors.Describe(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))),
            StringComparison.Ordinal);

    [Fact]
    public void UnknownError_IncludesInnerMessage()
        => Assert.Equal("outer (inner)", NetworkErrors.Describe(new HttpRequestException("outer", new IOException("inner"))));

    [Fact]
    public async Task Prowlarr_TestConnection_ReportsReadableSslError()
    {
        var http = new FakeHttp((_, _) => throw new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("UntrustedRoot")));
        var client = new ProwlarrClient(http, new FakeConfig());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetIndexersAsync(CancellationToken.None));

        Assert.StartsWith("Prowlarr injoignable : connexion HTTPS refusée", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prowlarr_WrongApiKey_IsExplicit()
    {
        var http = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new ProwlarrClient(http, new FakeConfig());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetIndexersAsync(CancellationToken.None));

        Assert.Contains("clé API", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prowlarr:9696")]
    [InlineData("ftp://nas")]
    public async Task Prowlarr_InvalidUrl_IsExplicit(string url)
    {
        var config = new FakeConfig();
        config.Current.ProwlarrUrl = url;
        var client = new ProwlarrClient(new FakeHttp((_, _) => FakeHttp.Json("[]")), config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetIndexersAsync(CancellationToken.None));

        Assert.Contains("URL de Prowlarr invalide", ex.Message, StringComparison.Ordinal);
    }
}
