using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public static class Queries
{
    public static Task<List<CheckResult>> LatestResultsAsync(this AppDbContext db) =>
        db.Results.Where(r => r.ObservedAt == db.Results
            .Where(x => x.AssetId == r.AssetId && x.CheckType == r.CheckType).Max(x => x.ObservedAt))
            .ToListAsync();

    public static CheckStatus? WorstStatus(this IEnumerable<CheckResult> results) =>
        results.Select(r => (CheckStatus?)r.Status).DefaultIfEmpty(null).Max();
}

public static class ResourceText
{
    /// <summary>Renders a stored "key|arg|arg" summary through the resource files.</summary>
    public static string Summary(Microsoft.Extensions.Localization.IStringLocalizer l, string summary)
    {
        var parts = summary.Split('|');
        return l[parts[0], parts.Skip(1).Cast<object>().ToArray()].Value;
    }

    public static IEnumerable<string> Issues(Microsoft.Extensions.Localization.IStringLocalizer l, CheckResult r) =>
        (r.Values.GetValueOrDefault("_issues") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => l["Issue_" + i].Value);
}
