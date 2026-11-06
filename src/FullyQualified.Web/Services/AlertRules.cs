namespace FullyQualified.Web;

/// <summary>Which recorded changes an organization can ask an alarm for, and which of its rules a change matches.</summary>
public static class AlertRules
{
    /// <summary>"Any change of this check".</summary>
    public const string AnyKey = "*";

    /// <summary>
    /// The values a check records and compares between runs (the keys of <see cref="Change"/>). The certificate-log search ("ct") is left out:
    /// it is run and reviewed by hand and never raises an alarm. Keep in step with the values the checks produce.
    /// </summary>
    public static readonly IReadOnlyList<(string CheckType, string Key)> Catalog =
    [
        ("dns", "ns"), ("dns", "mx"), ("dns", "a"), ("dns", "aaaa"), ("dns", "cname"),
        ("dns", "spf"), ("dns", "dmarc"), ("dns", "dkim"), ("dns", "caa"), ("dns", "dnssec"),
        ("tls", "issuer"), ("tls", "protocol"),
    ];

    public static IEnumerable<string> CheckTypes => Catalog.Select(c => c.CheckType).Distinct();

    public static bool IsKnown(string? checkType, string? key) =>
        checkType is not null && key is not null && (key == AnyKey ? CheckTypes.Contains(checkType) : Catalog.Contains((checkType, key)));

    /// <summary>"dkim" covers every selector ("dkim.google", "dkim.s1", ...).</summary>
    static bool Covers(string ruleKey, string changedKey) =>
        ruleKey == AnyKey || changedKey == ruleKey || changedKey.StartsWith(ruleKey + ".", StringComparison.Ordinal);

    /// <summary>The changes that at least one rule asked about. A change several rules match is reported once.</summary>
    public static List<(string Key, string Before, string After)> Hits(IEnumerable<AlertRule> rules, Guid assetId, string checkType,
        IEnumerable<(string Key, string Before, string After)> changes)
    {
        var mine = rules.Where(r => r.CheckType == checkType && (r.AssetId is null || r.AssetId == assetId)).ToList();
        return mine.Count == 0 ? [] : changes.Where(c => mine.Any(r => Covers(r.Key, c.Key))).ToList();
    }
}
