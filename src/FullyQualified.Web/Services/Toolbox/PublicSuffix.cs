using System.Globalization;

namespace FullyQualified.Web;

/// <summary>Full Mozilla PSL prevailing-rule matching. Only the immutable upstream rules are retained.</summary>
public static class PublicSuffix
{
    static readonly HashSet<string> Rules = Load();
    static HashSet<string> Load()
    {
        var assembly = typeof(PublicSuffix).Assembly;
        using var stream = assembly.GetManifestResourceStream("FullyQualified.Web.Data.public_suffix_list.dat")
            ?? throw new InvalidOperationException("Missing embedded public suffix data.");
        using var reader = new StreamReader(stream);
        var rules = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            var prefix = line.StartsWith('!') ? "!" : line.StartsWith("*.") ? "*." : "";
            rules.Add(prefix + new IdnMapping().GetAscii(line[prefix.Length..]).ToLowerInvariant());
        }
        return rules;
    }

    public static string? OrganizationalDomain(string domain)
    {
        var normalized = ToolNetwork.NormalizeHost(domain);
        if (normalized is null || System.Net.IPAddress.TryParse(normalized, out _)) return null;
        var labels = normalized.Split('.');
        var suffixLength = 1;
        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels[i..]);
            if (Rules.Contains("!" + suffix)) { suffixLength = labels.Length - i - 1; break; }
            if (Rules.Contains(suffix)) suffixLength = Math.Max(suffixLength, labels.Length - i);
            if (i > 0 && Rules.Contains("*." + suffix)) suffixLength = Math.Max(suffixLength, labels.Length - i + 1);
        }
        return labels.Length <= suffixLength ? null : string.Join('.', labels[^(suffixLength + 1)..]);
    }
}
