using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

/// <summary>Says how and why an asset was discovered, from what the CT check recorded.</summary>
public static class Discovery
{
    public static string Short(IStringLocalizer l, Asset a, Asset? parent)
    {
        var via = string.IsNullOrEmpty(a.DiscoveredVia) ? l["Disc_ViaCt"].Value : l["Disc_Via", a.DiscoveredVia].Value;
        return parent is null ? via : $"{via} · {l["Disc_From", parent.Name].Value}";
    }

    public static string Explain(IStringLocalizer l, Asset a, Asset? parent)
    {
        if (!a.Discovered) return l["Disc_Manual"].Value;
        var from = parent?.Name ?? l["Disc_UnknownParent"].Value;
        return a.DiscoveredIssuer is null || a.DiscoveredCertExpires is null
            ? l["Disc_Legacy", from].Value
            : l["Disc_Why", from, a.DiscoveredVia ?? "", a.DiscoveredIssuer, a.DiscoveredCertExpires.Value.ToString("yyyy-MM-dd"), a.CreatedAt.ToString("yyyy-MM-dd")].Value;
    }
}
