using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class TlsCheckTests
{
    static async Task<CheckOutcome> ScanAsync(int validDays, params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (dnsNames.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var n in dnsNames) san.AddDnsName(n);
            req.CertificateExtensions.Add(san.Build());
        }
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(validDays));
        using var server = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(server);
            await Task.Delay(200);
        });

        var check = new TlsCheck(new HostGuard(new ConfigurationBuilder().Build()));
        var outcome = await check.RunAsync(new Asset { Name = $"localhost:{port}" }, default);
        await serving;
        listener.Stop();
        return outcome;
    }

    [Fact]
    public async Task Untrusted_certificate_is_a_warning_and_is_inventoried()
    {
        var o = await ScanAsync(90);
        Assert.Equal(CheckStatus.Warning, o.Status);
        Assert.StartsWith("Sum_TlsChain", o.Summary);
        Assert.Single(o.Certs!);
    }

    [Fact]
    public async Task A_name_mismatch_says_what_was_checked_and_what_the_certificate_covers()
    {
        var o = await ScanAsync(90, "other.test", "www.other.test");
        Assert.Equal(CheckStatus.Warning, o.Status);
        Assert.StartsWith("Sum_TlsChainMismatch|", o.Summary);
        Assert.Contains("RemoteCertificateNameMismatch", o.Summary);
        Assert.Contains("|localhost|", o.Summary);                       // the name that was checked
        Assert.EndsWith("|other.test, www.other.test", o.Summary);       // and what the certificate is valid for
    }

    [Fact]
    public async Task Connection_failure_is_reported_as_error()
    {
        var check = new TlsCheck(new HostGuard(new ConfigurationBuilder().Build()));
        var o = await check.RunAsync(new Asset { Name = "localhost:1" }, default);
        Assert.Equal(CheckStatus.Error, o.Status);
    }
}
