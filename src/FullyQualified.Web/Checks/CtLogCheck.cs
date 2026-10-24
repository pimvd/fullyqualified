using System.Text.Json;

namespace FullyQualified.Web;

/// <summary>Discovers certificates and hostnames through the crt.sh Certificate Transparency API, falling back to Cert Spotter when crt.sh fails.</summary>
public sealed class CtLogCheck(IHttpClientFactory http) : ICheck
{
    const int MaxCerts = 200, MaxHosts = 500;

    public string Type => "ct";
    /// <summary>Discovery is optional and applies to names without a port that were not themselves discovered: it sends domain names to outside services, so it runs when someone presses the button.</summary>
    public bool Automatic => false;
    public bool AppliesTo(Asset asset) => !asset.Discovered && !asset.Name.Contains(':');

    /// <summary>One certificate as reported by a CT search service.</summary>
    record Entry(string[] Names, string IssuerDn, string Id, DateTimeOffset NotBefore, DateTimeOffset NotAfter);

    public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
    {
        var domain = asset.Name;
        using var client = http.CreateClient("ct");
        List<Entry> entries;
        var service = "crt.sh";
        try { entries = await FromCrtShAsync(client, domain, ct); service = "crt.sh"; }
        catch (Exception primary) when (IsFetchError(primary, ct))
        {
            // crt.sh is free and often overloaded (502, 429, timeouts); Cert Spotter answers the same question.
            try { entries = await FromCertSpotterAsync(client, domain, ct); service = "Cert Spotter"; }
            catch (Exception fallback) when (IsFetchError(fallback, ct))
            {
                return new(CheckStatus.Error, "Sum_Error|" + $"crt.sh: {primary.Message}; Cert Spotter: {fallback.Message}".Replace('|', '/'), []);
            }
        }
        return Summarize(domain, entries, service);
    }

    static bool IsFetchError(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    static async Task<List<Entry>> FromCrtShAsync(HttpClient client, string domain, CancellationToken ct)
    {
        using var stream = await client.GetStreamAsync($"https://crt.sh/?q=%25.{Uri.EscapeDataString(domain)}&output=json", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<Entry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!TryTime(e.GetProperty("not_after").GetString(), out var notAfter)) continue;
            TryTime(e.GetProperty("not_before").GetString(), out var notBefore);
            list.Add(new Entry(
                (e.GetProperty("name_value").GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                e.GetProperty("issuer_name").GetString() ?? "",
                e.GetProperty("serial_number").GetString() ?? e.GetProperty("id").ToString(), notBefore, notAfter));
        }
        return list;
    }

    static async Task<List<Entry>> FromCertSpotterAsync(HttpClient client, string domain, CancellationToken ct)
    {
        var url = $"https://api.certspotter.com/v1/issuances?domain={Uri.EscapeDataString(domain)}&include_subdomains=true&expand=dns_names&expand=issuer";
        using var stream = await client.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<Entry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!TryTime(e.GetProperty("not_after").GetString(), out var notAfter)) continue;
            TryTime(e.GetProperty("not_before").GetString(), out var notBefore);
            list.Add(new Entry(
                e.GetProperty("dns_names").EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToArray(),
                e.GetProperty("issuer").GetProperty("name").GetString() ?? "",
                e.GetProperty("cert_sha256").GetString() ?? e.GetProperty("id").ToString(), notBefore, notAfter));
        }
        return list;
    }

    static bool TryTime(string? s, out DateTimeOffset t) =>
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out t);

    static CheckOutcome Summarize(string domain, List<Entry> entries, string service)
    {
        var now = DateTimeOffset.UtcNow;
        var certs = new Dictionary<string, CertInfo>();
        var hosts = new Dictionary<string, DiscoveredHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.NotAfter > now))
        {
            // Remember the certificate that lists the name, preferring the one that stays valid longest.
            foreach (var n in e.Names.Where(n => !n.StartsWith('*') && n.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)).Select(n => n.ToLowerInvariant()))
                if (!hosts.TryGetValue(n, out var seen) || e.NotAfter > seen.CertExpires)
                    hosts[n] = new DiscoveredHost(n, service, CnOf(e.IssuerDn), e.NotAfter);
            if (certs.Count < MaxCerts && e.Names.Length > 0 && !certs.ContainsKey(e.Id))
                certs[e.Id] = new CertInfo(e.Names[0], e.Names[0], CnOf(e.IssuerDn), e.Id, e.NotBefore, e.NotAfter, "ct",
                    Renewal.Guess(e.IssuerDn, e.NotBefore, e.NotAfter));
        }
        var discovered = hosts.Values.OrderBy(h => h.Name, StringComparer.Ordinal).Take(MaxHosts).ToList();
        return new(CheckStatus.Ok, $"Sum_CtOk|{certs.Count}|{hosts.Count}",
            new() { ["hostnames"] = string.Join(", ", hosts.Keys.OrderBy(h => h, StringComparer.Ordinal)) }, certs.Values.ToList(), discovered);
    }

    static string CnOf(string dn) => dn.Split(',', StringSplitOptions.TrimEntries)
        .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..] ?? dn;
}
