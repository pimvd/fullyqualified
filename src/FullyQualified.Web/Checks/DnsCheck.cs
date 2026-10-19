using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

public sealed class DnsCheck(IConfiguration cfg) : ICheck
{
    static readonly string[] DkimSelectors = ["default", "selector1", "selector2", "google", "k1", "s1", "dkim"];

    readonly LookupClient dns = Create(cfg["Dns:Server"]);

    static LookupClient Create(string? server)
    {
        var options = new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 };
        if (!string.IsNullOrWhiteSpace(server))
            options = new LookupClientOptions(System.Net.IPAddress.Parse(server)) { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 };
        return new LookupClient(options);
    }

    public string Type => "dns";
    public bool AppliesTo(Asset asset) => asset.Kind != AssetKind.CloudResource;

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

    async Task<List<string>> Txt(string name, CancellationToken ct) =>
        (await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct)).Answers.TxtRecords()
            .Select(t => string.Concat(t.Text)).ToList();
}
