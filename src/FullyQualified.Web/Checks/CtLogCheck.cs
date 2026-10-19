using System.Text.Json;

namespace FullyQualified.Web;

/// <summary>Discovers certificates and hostnames through the crt.sh Certificate Transparency API.</summary>
public sealed class CtLogCheck(IHttpClientFactory http) : ICheck
{
    const int MaxCerts = 200, MaxHosts = 50;

    public string Type => "ct";
    public bool AppliesTo(Asset asset) => asset.Kind == AssetKind.Domain && !asset.Name.Contains(':');

    public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
    {
        var domain = asset.Name;
        using var client = http.CreateClient("ct");
        try
        {
            using var stream = await client.GetStreamAsync($"https://crt.sh/?q=%25.{Uri.EscapeDataString(domain)}&output=json", ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var now = DateTimeOffset.UtcNow;
            var certs = new Dictionary<string, CertInfo>();
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!DateTimeOffset.TryParse(e.GetProperty("not_after").GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var notAfter) || notAfter <= now) continue;
                DateTimeOffset.TryParse(e.GetProperty("not_before").GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var notBefore);
                var names = (e.GetProperty("name_value").GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var issuer = e.GetProperty("issuer_name").GetString() ?? "";
                var serial = e.GetProperty("serial_number").GetString() ?? e.GetProperty("id").ToString();
                foreach (var n in names.Where(n => !n.StartsWith('*') && n.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                    hosts.Add(n.ToLowerInvariant());
                if (certs.Count < MaxCerts && names.Length > 0 && !certs.ContainsKey(serial))
                    certs[serial] = new CertInfo(names[0], names[0], CnOf(issuer), serial, notBefore, notAfter, "ct",
                        Renewal.Guess(issuer, notBefore, notAfter));
            }

            var discovered = hosts.OrderBy(h => h).Take(MaxHosts).ToList();
            return new(CheckStatus.Ok, $"Sum_CtOk|{certs.Count}|{hosts.Count}",
                new() { ["hostnames"] = string.Join(", ", hosts.OrderBy(h => h)) }, certs.Values.ToList(), discovered);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(CheckStatus.Error, $"Sum_Error|{ex.Message.Replace('|', '/')}", []);
        }
    }

    static string CnOf(string dn) => dn.Split(',', StringSplitOptions.TrimEntries)
        .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..] ?? dn;
}
