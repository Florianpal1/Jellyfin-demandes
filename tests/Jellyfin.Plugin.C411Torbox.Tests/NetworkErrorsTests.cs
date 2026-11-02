using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class NetworkErrorsTests
{
    [Fact]
    public void SslError_WithoutDiagnostic_PointsToLogs()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid: UntrustedRoot"));

        var message = NetworkErrors.Describe(ex);

        Assert.Contains("UntrustedRoot", message, StringComparison.Ordinal);
        Assert.Contains("journaux Jellyfin", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SslError_WithDiagnostic_GivesTheReason()
    {
        var ex = new HttpRequestException("ssl", new AuthenticationException("rejected by the provided RemoteCertificateValidationCallback"));

        var message = NetworkErrors.Describe(ex, "chaîne incomplète");

        Assert.Equal("certificat HTTPS refusé par le NAS : chaîne incomplète.", message);
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.PartialChain, "chaîne incomplète")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot, "autorité racine inconnue du NAS")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.NotTimeValid, "certificat expiré")]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, X509ChainStatusFlags.NoError, "le nom de l'URL ne correspond pas")]
    public void Explain_TranslatesCertificateProblems(SslPolicyErrors errors, X509ChainStatusFlags status, string expected)
        => Assert.Contains(expected, CertificateDiagnostics.Explain(errors, [status]), StringComparison.Ordinal);

    [Fact]
    public async Task Prowlarr_ShowsRecordedCertificateReason()
    {
        var diagnostics = new CertificateDiagnostics();
        diagnostics.Record("prowlarr", "chaîne incomplète");
        var http = new FakeHttp((_, _) => throw new HttpRequestException("ssl", new AuthenticationException("rejected")));
        var client = new ProwlarrClient(http, new FakeConfig(), diagnostics);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetIndexersAsync(CancellationToken.None));

        Assert.Equal("Prowlarr injoignable : certificat HTTPS refusé par le NAS : chaîne incomplète.", ex.Message);
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

public class CertificateContextTests
{
    [Fact]
    public void DescribeContext_ListsCertificateNamesAndResolvedAddresses()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=autre-site.example", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("autre-site.example");
        san.AddDnsName("www.autre-site.example");
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var text = CertificateDiagnostics.DescribeContext("localhost", cert);

        Assert.Contains("certificat reçu pour : autre-site.example, www.autre-site.example", text, StringComparison.Ordinal);
        Assert.Contains("localhost résolu par le NAS en : ", text, StringComparison.Ordinal);
    }
}
