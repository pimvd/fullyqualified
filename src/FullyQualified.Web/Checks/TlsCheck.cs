using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FullyQualified.Web;

public sealed class TlsCheck(HostGuard guard) : ICheck
{
    public string Type => "tls";
    public bool AppliesTo(Asset asset) => asset.Kind != AssetKind.CloudResource;

    public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
    {
        var (host, port) = Targets.Parse(asset.Name);
        if (!await guard.IsAllowedAsync(host, ct))
            return new(CheckStatus.Error, "Sum_Blocked", []);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            var errors = SslPolicyErrors.None;
            X509Certificate2? leaf = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, e) =>
            {
                errors = e;
                if (cert is not null) leaf = new X509Certificate2(cert);
                return true; // inspect even broken certificates; we report the errors instead
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            if (leaf is null) return new(CheckStatus.Error, "Sum_Error|no certificate presented", []);

            var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var issuer = leaf.GetNameInfo(X509NameType.SimpleName, true);
            var info = new CertInfo(host, leaf.GetNameInfo(X509NameType.SimpleName, false), issuer,
                leaf.GetCertHashString(HashAlgorithmName.SHA256), notBefore, notAfter, "endpoint",
                Renewal.Guess(leaf.IssuerName.Name, notBefore, notAfter));
            var days = (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays);
            var values = new Dictionary<string, string> { ["issuer"] = issuer, ["protocol"] = ssl.SslProtocol.ToString() };

            var (status, summary) = true switch
            {
                _ when days < 0 => (CheckStatus.Critical, $"Sum_TlsExpired|{notAfter:yyyy-MM-dd}"),
                _ when errors != SslPolicyErrors.None => (CheckStatus.Warning, $"Sum_TlsChain|{errors}"),
                _ when days <= 14 => (CheckStatus.Critical, $"Sum_TlsOk|{days}|{notAfter:yyyy-MM-dd}|{issuer}"),
                _ when days <= 30 => (CheckStatus.Warning, $"Sum_TlsOk|{days}|{notAfter:yyyy-MM-dd}|{issuer}"),
                _ => (CheckStatus.Ok, $"Sum_TlsOk|{days}|{notAfter:yyyy-MM-dd}|{issuer}"),
            };
            return new(status, summary, values, [info]);
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(CheckStatus.Error, $"Sum_Error|{ex.Message.Replace('|', '/')}", []);
        }
    }
}
