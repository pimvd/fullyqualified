using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FullyQualified.Web;

public sealed class TlsCheck(HostGuard guard) : ICheck
{
    public string Type => "tls";
    public bool AppliesTo(Asset asset) => true;

    public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
    {
        var (host, port) = Targets.Parse(asset.Name);
        var target = await guard.ResolveAsync(host, ct);
        if (target.Unresolved) return new(CheckStatus.Error, "Sum_Error|host not found", []);
        if (!target.Allowed) return new(CheckStatus.Error, "Sum_Blocked", []);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var tcp = new TcpClient();
            // Connect to the address that was checked, not to the name again.
            if (target.Address is { } pinned) await tcp.ConnectAsync(pinned, port, cts.Token);
            else await tcp.ConnectAsync(host, port, cts.Token);
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
                _ when errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) =>
                    (CheckStatus.Warning, $"Sum_TlsChainMismatch|{errors}|{host}|{CoveredNames(leaf)}"),
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

    /// <summary>The names the certificate is valid for: its DNS subject alternative names, or the subject name when it has none.</summary>
    static string CoveredNames(X509Certificate2 cert)
    {
        var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().Distinct().ToList() ?? [];
        if (names.Count == 0) names.Add(cert.GetNameInfo(X509NameType.SimpleName, false));
        const int max = 8;
        var shown = string.Join(", ", names.Take(max)).Replace('|', '/');
        return names.Count > max ? $"{shown} (+{names.Count - max})" : shown;
    }
}
