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

/// <param name="Allowed">False when the host may not be scanned.</param>
/// <param name="Address">When set, connect to exactly this address: it is the one that was checked, so DNS cannot change its answer in between.</param>
/// <param name="Unresolved">The name did not resolve. Only meaningful when <paramref name="Allowed"/> is false.</param>
public record GuardResult(bool Allowed, System.Net.IPAddress? Address = null, bool Unresolved = false);

/// <summary>Keeps scans away from private addresses when the install is not allowed to reach them.</summary>
public class HostGuard
{
    readonly bool allowPrivate;

    public HostGuard(IConfiguration cfg) : this(cfg.GetValue("Scanning:AllowPrivateAddresses", true)) { }

    public HostGuard(bool allowPrivate) => this.allowPrivate = allowPrivate;

    /// <summary>Always refuses private and local addresses, whatever the configuration says. For scans anyone on the internet can ask for.</summary>
    public static HostGuard Strict() => new(false);

    public async Task<bool> IsAllowedAsync(string host, CancellationToken ct) => (await ResolveAsync(host, ct)) is { Allowed: true } or { Unresolved: true };

    public async Task<GuardResult> ResolveAsync(string host, CancellationToken ct)
    {
        if (allowPrivate) return new(true);
        System.Net.IPAddress[] addrs;
        try { addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct); }
        catch (System.Net.Sockets.SocketException) { return new(false, null, Unresolved: true); }
        catch (ArgumentException) { return new(false); } // an unspecified address such as 0.0.0.0 is not a target
        if (addrs.Length == 0) return new(false, null, Unresolved: true);
        return addrs.Any(IsPrivate) ? new(false) : new(true, addrs[0]);
    }

    static bool IsPrivate(System.Net.IPAddress a)
    {
        if (System.Net.IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] is 10 or 0 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0) || b[0] >= 224;
    }
}
