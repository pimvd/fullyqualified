using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FullyQualified.Web;

public static partial class Dkim
{
    public const int MaxSelectors = 10;

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]{0,60}[a-z0-9])?$")]
    private static partial Regex Valid();

    /// <summary>Splits "a, b;c" into distinct lower-case selectors. Returns null when any of them is not a valid DNS label sequence or there are too many.</summary>
    public static List<string>? ParseSelectors(string? input)
    {
        var list = (input ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct().ToList();
        return list.Count <= MaxSelectors && list.All(s => Valid().IsMatch(s)) ? list : null;
    }

    public enum State { Ok, Missing, Revoked, Invalid }

    public record Analysis(State State, int KeyBits, string Fingerprint);

    /// <summary>Reads the TXT records found at selector._domainkey.domain.</summary>
    public static Analysis Analyze(IEnumerable<string> txtRecords)
    {
        var records = txtRecords.ToList();
        var record = records.FirstOrDefault(t => t.Contains("v=DKIM1", StringComparison.OrdinalIgnoreCase))
                     ?? records.FirstOrDefault(t => PTag().IsMatch(t));
        if (record is null) return new(State.Missing, 0, "");

        var tags = record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim(), StringComparer.Ordinal);
        var p = new string((tags.GetValueOrDefault("p") ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (p.Length == 0) return new(State.Revoked, 0, "");

        try
        {
            var der = Convert.FromBase64String(p);
            var fingerprint = Convert.ToHexString(SHA256.HashData(der))[..8].ToLowerInvariant();
            if (string.Equals(tags.GetValueOrDefault("k"), "ed25519", StringComparison.OrdinalIgnoreCase)) return new(State.Ok, 256, fingerprint);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(der, out _);
            return new(State.Ok, rsa.KeySize, fingerprint);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return new(State.Invalid, 0, ""); }
    }

    [GeneratedRegex(@"(^|;)\s*p=", RegexOptions.IgnoreCase)]
    private static partial Regex PTag();
}

public static class ResolverCompare
{
    /// <summary>Record types whose answers differ between the primary resolver and any other resolver that answered.</summary>
    public static List<string> Differences(IReadOnlyDictionary<string, string> primary,
        IEnumerable<IReadOnlyDictionary<string, string>> others) =>
        primary.Keys.Where(type => others.Any(o => o.TryGetValue(type, out var v) && v != primary[type])).OrderBy(t => t).ToList();
}
