using System.Text.RegularExpressions;

namespace FullyQualified.Web;

public static partial class HostName
{
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}(:\d{1,5})?$")]
    private static partial Regex Valid();

    /// <summary>Accepts "example.com", "https://example.com/path" or "host:8443"; returns null when invalid.</summary>
    public static string? Normalize(string? input)
    {
        var s = (input ?? "").Trim().ToLowerInvariant();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        s = s.Split('/', '?', '#')[0].TrimEnd('.');
        if (!Valid().IsMatch(s)) return null;
        var colon = s.LastIndexOf(':');
        return colon > 0 && int.Parse(s[(colon + 1)..]) is 0 or > 65535 ? null : s;
    }
}
