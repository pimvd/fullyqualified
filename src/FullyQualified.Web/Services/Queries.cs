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
    /// <summary>"15 min", "6 h", "1 d": the largest unit that divides the interval evenly.</summary>
    public static string Interval(Microsoft.Extensions.Localization.IStringLocalizer l, int minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440} {l["Unit_Day"]}" : minutes % 60 == 0 ? $"{minutes / 60} {l["Unit_Hour"]}" : $"{minutes} {l["Unit_Minute"]}";

    /// <summary>"1 month", "12 months".</summary>
    public static string Months(Microsoft.Extensions.Localization.IStringLocalizer l, int months) => months == 1 ? l["Plan_Month"].Value : l["Plan_Months", months].Value;

    /// <summary>Renders a stored "key|arg|arg" summary through the resource files.</summary>
    public static string Summary(Microsoft.Extensions.Localization.IStringLocalizer l, string summary)
    {
        var parts = summary.Split('|');
        return l[parts[0], parts.Skip(1).Cast<object>().ToArray()].Value;
    }

    public static IEnumerable<string> Issues(Microsoft.Extensions.Localization.IStringLocalizer l, CheckResult r) =>
        (r.Values.GetValueOrDefault("_issues") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => l["Issue_" + i].Value);
}
