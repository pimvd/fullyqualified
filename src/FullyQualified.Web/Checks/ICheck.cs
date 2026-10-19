namespace FullyQualified.Web;

public record CertInfo(string Host, string Subject, string Issuer, string Fingerprint,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Source, string Renewal);

/// <param name="Summary">Resource key, optionally followed by '|'-separated arguments.</param>
/// <param name="Values">Baseline values; a difference with the previous run is recorded as a change.
/// Keys starting with '_' are informational and never produce changes.</param>
public record CheckOutcome(CheckStatus Status, string Summary, Dictionary<string, string> Values,
    List<CertInfo>? Certs = null, List<string>? DiscoveredHosts = null);

/// <summary>Every check is a plugin behind this interface.</summary>
public interface ICheck
{
    string Type { get; }
    bool AppliesTo(Asset asset);
    Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct);
}

public static class Targets
{
    public static (string Host, int Port) Parse(string name)
    {
        var i = name.LastIndexOf(':');
        return i > 0 && int.TryParse(name[(i + 1)..], out var p) ? (name[..i], p) : (name, 443);
    }
}

public static class Renewal
{
    static readonly string[] AcmeIssuers = ["Let's Encrypt", "ZeroSSL", "Google Trust Services", "Buypass", "SSL.com"];

    /// <summary>Best-effort guess: ACME CAs and short-lived certificates are almost always automated.</summary>
    public static string Guess(string fullIssuer, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        AcmeIssuers.Any(a => fullIssuer.Contains(a, StringComparison.OrdinalIgnoreCase))
        || (notAfter - notBefore).TotalDays <= 100 ? "Automated" : "Manual";
}

/// <summary>Keeps scans away from private addresses when the install is not allowed to reach them.</summary>
public class HostGuard(IConfiguration cfg)
{
    readonly bool allowPrivate = cfg.GetValue("Scanning:AllowPrivateAddresses", true);

    public async Task<bool> IsAllowedAsync(string host, CancellationToken ct)
    {
        if (allowPrivate) return true;
        System.Net.IPAddress[] addrs;
        try { addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct); }
        catch (System.Net.Sockets.SocketException) { return true; }
        return addrs.All(a => !IsPrivate(a));
    }

    static bool IsPrivate(System.Net.IPAddress a)
    {
        if (System.Net.IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] is 10 or 0 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
