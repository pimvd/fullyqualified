namespace FullyQualified.Web;

/// <summary>One block of numbered steps; <see cref="HeadingKey"/> names the situation when a finding has several.</summary>
public record RemedySection(string? HeadingKey, string StepsKey);

/// <param name="Base">Resource key prefix: <c>Fix_{Base}_What</c> explains the finding and its risk.</param>
/// <param name="Args">Fill the placeholders of the resource strings: usually the name, then something specific to the finding.</param>
/// <param name="Commands">Things to run to see the problem for yourself, with the real name filled in.</param>
/// <param name="Example">A record or configuration line to start from.</param>
/// <param name="Lookups">Records that the DNS lookup window can show, as (name, type).</param>
public record Remedy(string Base, object[] Args, List<RemedySection> Sections, List<string> Commands, string? Example, List<(string Name, string Type)> Lookups)
{
    public string WhatKey => $"Fix_{Base}_What";
}

/// <summary>How to fix each finding: what it means, what to do, and commands and examples with the asset's own names in them.</summary>
public static class Remedies
{
    public const string Fallback = "Check";

    /// <summary>Every finding the checks can report, as codes (with an example argument where the code carries one). A test holds the catalogue to this list.</summary>
    public static readonly string[] AllCodes =
    [
        "NoSpf", "NoNullMx", "NoDmarc", "DmarcNone", "DmarcQuarantine", "DmarcSubdomainNotReject", "NoCaa", "NoNs", "DanglingCname", "ResolverMismatch:ns", "DkimMissing:s1", "DkimRevoked:s1", "DkimInvalid:s1", "DkimWeak:s1:512", "DnsError",
        "TlsExpired", "TlsExpiring", "TlsTrust", "TlsError", "CtError",
    ];

    /// <summary>The page for one finding of one asset. Anchored on what the check stored, so it can name the certificate's names or the CNAME target.</summary>
    public static string Link(string? origin, Guid assetId, string check, string code) =>
        $"{(origin ?? "").TrimEnd('/')}/fix?asset={assetId}&check={Uri.EscapeDataString(check)}&code={Uri.EscapeDataString(code)}";

    public static Remedy? For(string code, Asset asset, CheckResult? latest)
    {
        var parts = code.Split(':');
        var (host, port) = Targets.Parse(asset.Name);
        var domain = host;
        var values = latest?.Values ?? [];
        var summary = (latest?.Summary ?? "").Split('|');
        string Arg(int i) => i < summary.Length ? summary[i] : "";

        List<(string, string)> Dig(params (string, string)[] items) => items.ToList();
        RemedySection Steps(string key) => new(null, key);

        switch (parts[0])
        {
            case "NoSpf":
                return new("NoSpf", [domain], [Steps("Fix_NoSpf_Steps")], [$"dig +short TXT {domain}"],
                    "v=spf1 include:_spf.your-mail-provider.example ~all", Dig((domain, "TXT")));
            case "NoDmarc":
                return new("NoDmarc", [domain], [Steps("Fix_NoDmarc_Steps")], [$"dig +short TXT _dmarc.{domain}"],
                    $"_dmarc.{domain}  TXT  \"v=DMARC1; p=none; rua=mailto:dmarc@{domain}\"", Dig(($"_dmarc.{domain}", "TXT")));
            case "DmarcNone":
                return new("DmarcNone", [domain], [Steps("Fix_DmarcNone_Steps")], [$"dig +short TXT _dmarc.{domain}"],
                    $"_dmarc.{domain}  TXT  \"v=DMARC1; p=quarantine; pct=25; rua=mailto:dmarc@{domain}\"", Dig(($"_dmarc.{domain}", "TXT")));
            case "NoNullMx":
                return new("NoNullMx", [domain], [Steps("Fix_NoNullMx_Steps")], [$"dig +short MX {domain}"],
                    $"{domain}  MX  0 .\n{domain}  TXT  \"v=spf1 -all\"", Dig((domain, "MX")));
            case "DmarcQuarantine":
                return new("DmarcQuarantine", [domain], [Steps("Fix_DmarcQuarantine_Steps")], [$"dig +short TXT _dmarc.{domain}"],
                    $"_dmarc.{domain}  TXT  \"v=DMARC1; p=reject; rua=mailto:dmarc@{domain}\"", Dig(($"_dmarc.{domain}", "TXT")));
            case "DmarcSubdomainNotReject":
                return new("DmarcSubdomainNotReject", [domain], [Steps("Fix_DmarcSubdomainNotReject_Steps")], [$"dig +short TXT _dmarc.{domain}"],
                    $"_dmarc.{domain}  TXT  \"v=DMARC1; p=reject; sp=reject; rua=mailto:dmarc@{domain}\"", Dig(($"_dmarc.{domain}", "TXT")));
            case "NoCaa":
                return new("NoCaa", [domain], [Steps("Fix_NoCaa_Steps")], [$"dig +short CAA {domain}"],
                    $"{domain}  CAA  0 issue \"letsencrypt.org\"\n{domain}  CAA  0 iodef \"mailto:security@{domain}\"", Dig((domain, "CAA")));
            case "NoNs":
                return new("NoNs", [domain], [Steps("Fix_NoNs_Steps")], [$"dig NS {domain} +trace", $"whois {domain}"], null, Dig((domain, "NS")));
            case "DanglingCname":
            {
                var target = values.GetValueOrDefault("cname") ?? "";
                return new("DanglingCname", [domain, target], [Steps("Fix_DanglingCname_Steps")],
                    [$"dig +short CNAME {domain}", $"dig +short A {target}"], null, target.Length > 0 ? Dig((domain, "CNAME"), (target, "A")) : Dig((domain, "CNAME")));
            }
            case "ResolverMismatch":
            {
                var type = (parts.Length > 1 ? parts[1] : "ns").ToUpperInvariant();
                return new("ResolverMismatch", [domain, type], [Steps("Fix_ResolverMismatch_Steps")],
                    [$"dig +short {type} {domain} @1.1.1.1", $"dig +short {type} {domain} @8.8.8.8", $"dig +short NS {domain}", $"dig +short {type} {domain} @<nameserver from the line above>"],
                    null, Dig((domain, type)));
            }
            case "DkimMissing" or "DkimRevoked" or "DkimInvalid" or "DkimWeak":
            {
                var selector = parts.Length > 1 ? parts[1] : "selector1";
                var bits = parts.Length > 2 ? parts[2] : "";
                var name = $"{selector}._domainkey.{domain}";
                var steps = parts[0] switch { "DkimMissing" => "Fix_DkimMissing_Steps", "DkimRevoked" => "Fix_DkimRevoked_Steps", "DkimInvalid" => "Fix_DkimInvalid_Steps", _ => "Fix_DkimWeak_Steps" };
                return new(parts[0], [domain, selector, bits], [Steps(steps)], [$"dig +short TXT {name}"],
                    parts[0] == "DkimMissing" ? $"{name}  TXT  \"v=DKIM1; k=rsa; p=<public key from your mail provider>\"" : null, Dig((name, "TXT")));
            }
            case "DnsError":
                return new("DnsError", [domain, Arg(1)], [Steps("Fix_DnsError_Steps")], [$"dig {domain} A", $"dig {domain} A @1.1.1.1", $"dig +dnssec {domain} SOA"], null, Dig((domain, "A")));

            case "TlsExpired" or "TlsExpiring":
            {
                var expired = parts[0] == "TlsExpired";
                var date = expired ? Arg(1) : Arg(2);
                var days = expired ? "" : Arg(1);
                return new(parts[0], [host, date, days], [Steps("Fix_Tls_Renew_Steps")], [OpenSsl(host, port, "-noout -dates -issuer -subject")], null, Dig((host, "A")));
            }
            case "TlsTrust":
            {
                var errors = Arg(1);
                var mismatch = summary[0] == "Sum_TlsChainMismatch" || errors.Contains("NameMismatch", StringComparison.Ordinal);
                var chain = errors.Contains("ChainErrors", StringComparison.Ordinal) || errors.Contains("NotAvailable", StringComparison.Ordinal) || !mismatch;
                var sections = new List<RemedySection>();
                if (mismatch) sections.Add(new("Fix_TlsTrust_Mismatch_Title", "Fix_TlsTrust_Mismatch_Steps"));
                if (chain) sections.Add(new("Fix_TlsTrust_Chain_Title", "Fix_TlsTrust_Chain_Steps"));
                if (sections.Count == 1) sections[0] = sections[0] with { HeadingKey = null };
                var names = mismatch ? (Arg(3) is { Length: > 0 } listed ? listed : "…") : ""; // results from before the names were recorded have none
                return new("TlsTrust", [host, names, errors], sections,
                    [OpenSsl(host, port, "-noout -issuer -subject -ext subjectAltName"), $"openssl s_client -connect {host}:{port} -servername {host} -showcerts </dev/null"], null, Dig((host, "A")));
            }
            case "TlsError":
                return new("TlsError", [host, port, Arg(1)], [Steps("Fix_TlsError_Steps")],
                    [$"nc -vz {host} {port}", $"openssl s_client -connect {host}:{port} -servername {host}", $"curl -vI https://{host}:{port}/"], null, Dig((host, "A")));
            case "CtError":
                return new("CtError", [domain, Arg(1)], [Steps("Fix_CtError_Steps")], [$"curl -sI \"https://crt.sh/?q={domain}&output=json\""], null, []);
            default:
                return null;
        }
    }

    static string OpenSsl(string host, int port, string show) =>
        $"openssl s_client -connect {host}:{port} -servername {host} </dev/null 2>/dev/null | openssl x509 {show}";
}
