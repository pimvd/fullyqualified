using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

public sealed class DnsCheck(IConfiguration cfg) : ICheck
{
    static readonly string[] DkimSelectors = ["default", "selector1", "selector2", "google", "k1", "s1", "dkim"];

    readonly LookupClient dns = Create(cfg["Dns:Server"]);

    /// <summary>Other public resolvers used to cross-check NS, MX and CAA answers. Set Dns:CompareResolvers to a comma-separated list of IPs, or to empty to turn it off.</summary>
    readonly List<(string Ip, LookupClient Client)> others = (cfg["Dns:CompareResolvers"] ?? "1.1.1.1,9.9.9.9,8.8.8.8")
        .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Where(ip => System.Net.IPAddress.TryParse(ip, out _))
        .Select(ip => (ip, new LookupClient(new LookupClientOptions(System.Net.IPAddress.Parse(ip)) { UseCache = false, Timeout = TimeSpan.FromSeconds(3), Retries = 0 })))
        .ToList();

    static LookupClient Create(string? server)
    {
        var options = new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 };
        if (!string.IsNullOrWhiteSpace(server))
            options = new LookupClientOptions(System.Net.IPAddress.Parse(server)) { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 };
        return new LookupClient(options);
    }

    public string Type => "dns";
    public bool AppliesTo(Asset asset) => true;

    public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
    {
        var (name, _) = Targets.Parse(asset.Name);
        var issues = new List<string>();
        var values = new Dictionary<string, string>();

        try
        {
            // Dangling CNAME: the record points to a name that no longer exists.
            var cname = (await dns.QueryAsync(name, QueryType.CNAME, cancellationToken: ct)).Answers.CnameRecords().FirstOrDefault();
            if (cname is not null)
            {
                var target = cname.CanonicalName.Value.TrimEnd('.');
                values["cname"] = target;
                var resolved = await dns.QueryAsync(target, QueryType.A, cancellationToken: ct);
                if (resolved.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
                    issues.Add("DanglingCname");
            }

            // Addresses are part of the baseline: a changed A or AAAA record is how a hijacked name starts to point elsewhere.
            values["a"] = string.Join(", ", await Addresses(name, QueryType.A, ct));
            values["aaaa"] = string.Join(", ", await Addresses(name, QueryType.AAAA, ct));

            if (asset.Kind == AssetKind.Domain)
            {
                var txt = await Txt(name, ct);
                var spf = txt.FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)) ?? "";
                var dmarc = (await Txt("_dmarc." + name, ct)).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)) ?? "";
                var caa = (await dns.QueryAsync(name, QueryType.CAA, cancellationToken: ct)).Answers.CaaRecords()
                    .Select(c => $"{c.Tag} {c.Value}").OrderBy(x => x).ToList();
                var mx = (await dns.QueryAsync(name, QueryType.MX, cancellationToken: ct)).Answers.MxRecords()
                    .OrderBy(m => m.Preference).Select(m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}").ToList();
                var ns = (await dns.QueryAsync(name, QueryType.NS, cancellationToken: ct)).Answers.NsRecords()
                    .Select(n => n.NSDName.Value.TrimEnd('.')).OrderBy(x => x).ToList();
                var ds = (await dns.QueryAsync(name, QueryType.DS, cancellationToken: ct)).Answers.OfType<DsRecord>().Any();

                var dkim = new List<string>();
                foreach (var selector in DkimSelectors)
                    if ((await Txt($"{selector}._domainkey.{name}", ct)).Any(t => t.Contains("v=DKIM1", StringComparison.OrdinalIgnoreCase)))
                        dkim.Add(selector);

                foreach (var selector in Dkim.ParseSelectors(asset.DkimSelectors) ?? [])
                {
                    var found = Dkim.Analyze(await Txt($"{selector}._domainkey.{name}", ct));
                    values[$"dkim.{selector}"] = found.State switch
                    {
                        Dkim.State.Ok => $"{found.KeyBits}-bit key {found.Fingerprint}",
                        _ => found.State.ToString().ToLowerInvariant(),
                    };
                    switch (found.State)
                    {
                        case Dkim.State.Missing: issues.Add($"DkimMissing:{selector}"); break;
                        case Dkim.State.Revoked: issues.Add($"DkimRevoked:{selector}"); break;
                        case Dkim.State.Invalid: issues.Add($"DkimInvalid:{selector}"); break;
                        case Dkim.State.Ok when found.KeyBits < 1024: issues.Add($"DkimWeak:{selector}:{found.KeyBits}"); break;
                    }
                }

                values["spf"] = spf;
                values["dmarc"] = dmarc;
                values["caa"] = string.Join("; ", caa);
                values["mx"] = string.Join("; ", mx);
                values["ns"] = string.Join("; ", ns);
                values["dnssec"] = ds ? "yes" : "no";
                values["dkim"] = string.Join(", ", dkim);

                if (spf == "") issues.Add("NoSpf");
                if (dmarc == "") issues.Add("NoDmarc");
                else if (dmarc.Contains("p=none", StringComparison.OrdinalIgnoreCase)) issues.Add("DmarcNone");
                if (caa.Count == 0) issues.Add("NoCaa");
                if (ns.Count == 0) issues.Add("NoNs");

                var mismatched = await CompareResolversAsync(name, new Dictionary<string, string>
                {
                    ["ns"] = string.Join("; ", ns), ["mx"] = string.Join("; ", mx), ["caa"] = string.Join("; ", caa),
                }, values, ct);
                foreach (var type in mismatched) issues.Add($"ResolverMismatch:{type}");
            }
        }
        catch (DnsResponseException ex)
        {
            return new(CheckStatus.Error, $"Sum_Error|{ex.Message.Replace('|', '/')}", values);
        }

        values["_issues"] = string.Join(",", issues);
        var status = issues.Contains("DanglingCname") ? CheckStatus.Critical : issues.Count > 0 ? CheckStatus.Warning : CheckStatus.Ok;
        return new(status, issues.Count == 0 ? "Sum_DnsOk" : $"Sum_DnsIssues|{issues.Count}", values);
    }

    async Task<List<string>> Addresses(string name, QueryType type, CancellationToken ct) =>
        (await dns.QueryAsync(name, type, cancellationToken: ct)).Answers.AddressRecords()
            .Select(a => a.Address.ToString()).OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// <summary>Asks the other resolvers the same questions. Resolvers that do not answer (blocked port 53, timeout) are ignored.
    /// Differences are reported as issues; what each resolver said is kept in the informational "_resolvers" value.</summary>
    async Task<List<string>> CompareResolversAsync(string name, Dictionary<string, string> primary, Dictionary<string, string> values, CancellationToken ct)
    {
        if (others.Count == 0) return [];
        var answers = await Task.WhenAll(others.Select(async o =>
        {
            try
            {
                var ns = (await o.Client.QueryAsync(name, QueryType.NS, cancellationToken: ct)).Answers.NsRecords()
                    .Select(n => n.NSDName.Value.TrimEnd('.')).OrderBy(x => x).ToList();
                var mx = (await o.Client.QueryAsync(name, QueryType.MX, cancellationToken: ct)).Answers.MxRecords()
                    .OrderBy(m => m.Preference).Select(m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}").ToList();
                var caa = (await o.Client.QueryAsync(name, QueryType.CAA, cancellationToken: ct)).Answers.CaaRecords()
                    .Select(c => $"{c.Tag} {c.Value}").OrderBy(x => x).ToList();
                return (o.Ip, Answer: (Dictionary<string, string>?)new() { ["ns"] = string.Join("; ", ns), ["mx"] = string.Join("; ", mx), ["caa"] = string.Join("; ", caa) });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is DnsResponseException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                return (o.Ip, Answer: null);
            }
        }));

        var responded = answers.Where(a => a.Answer is not null).ToList();
        values["_resolvers"] = string.Join(" | ", responded.Select(r => $"{r.Ip}: " + string.Join(", ", r.Answer!.Select(kv => $"{kv.Key}={kv.Value}"))));
        return ResolverCompare.Differences(primary, responded.Select(r => (IReadOnlyDictionary<string, string>)r.Answer!));
    }

    async Task<List<string>> Txt(string name, CancellationToken ct) =>
        (await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct)).Answers.TxtRecords()
            .Select(t => string.Concat(t.Text)).ToList();
}
