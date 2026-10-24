namespace FullyQualified.Web;

/// <summary>A DMARC record and where it was found. <paramref name="Source"/> is the name that carries it (the asset itself or a parent).</summary>
public record DmarcFound(string Record, string Source, string Policy, string? SubdomainPolicy);

/// <summary>The mail rules of ADR 0011, decided from one name and its DNS answers.</summary>
public static class MailRules
{
    /// <summary>Sends mail: an MX other than the null MX, or an SPF record that is not just "v=spf1 -all".</summary>
    public static bool SendsMail(IEnumerable<string> mx, string spf) =>
        mx.Any(m => !IsNullMx(m)) || (spf != "" && !IsDenyAllSpf(spf));

    /// <summary>MX values read "preference exchange"; the null MX is "0 ." (RFC 7505).</summary>
    public static bool IsNullMx(string mx) => mx.Replace(" ", "") == "0.";

    public static bool IsDenyAllSpf(string spf) =>
        string.Concat(spf.Where(c => !char.IsWhiteSpace(c))).Equals("v=spf1-all", StringComparison.OrdinalIgnoreCase);

    public static bool IsSpf(string txt) => txt.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)
        && (txt.Length == 6 || char.IsWhiteSpace(txt[6]));

    public static bool IsDmarc(string txt) => txt.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)
        && (txt.Length == 8 || txt[8] is ';' or ' ' or '\t');

    /// <summary>The names to ask, in order: _dmarc.name, then one label less each time, stopping when one label is left (no public suffix list).</summary>
    public static IEnumerable<string> DmarcWalk(string name)
    {
        var labels = name.Trim('.').Split('.');
        for (var i = 0; i == 0 || labels.Length - i >= 2; i++) yield return string.Join('.', labels[i..]);
    }

    /// <summary>Reads the tags of a DMARC record. A missing or unknown p counts as none; sp is only set when the record has one.</summary>
    public static DmarcFound ParseDmarc(string record, string source)
    {
        var tags = record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].ToLowerInvariant());
        static string Policy(string? v) => v is "quarantine" or "reject" ? v : "none";
        return new(record, source, Policy(tags.GetValueOrDefault("p")), tags.TryGetValue("sp", out var sp) ? Policy(sp) : null);
    }

    /// <summary>The policy that applies to <paramref name="name"/>: sp when the record belongs to a parent and has one, otherwise p.</summary>
    public static string AppliedPolicy(DmarcFound found, string name) =>
        found.Source != name && found.SubdomainPolicy is { } sp ? sp : found.Policy;
}
