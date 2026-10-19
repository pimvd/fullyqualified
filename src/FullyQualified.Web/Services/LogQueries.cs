using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record LogFilter(int Days = 30, CheckStatus? Status = null, string? Check = null, string? Asset = null, int Page = 1);

public record LogRow(DateTimeOffset At, string Asset, Guid AssetId, string Check, CheckStatus Status, string Summary);

public record LogModel(
    LogFilter Filter, DateOnly From, DateOnly To,
    Dictionary<DateOnly, int[]> ResultsPerDay, Dictionary<DateOnly, int> ChangesPerDay, Dictionary<string, int[]> ByCheck,
    int Total, int Ok, int AlertsSent, int AlertsFailed,
    List<LogRow> Rows, int RowCount, int PageSize, List<AlertLogEntry> Alerts);

/// <summary>Everything the log screen shows, for the signed-in tenant (the context filter does the scoping).</summary>
public class LogQueries(AppDbContext db)
{
    public const int PageSize = 25;
    public static readonly int[] Ranges = [7, 30, 90];
    static readonly CheckStatus[] Order = [CheckStatus.Ok, CheckStatus.Warning, CheckStatus.Error, CheckStatus.Critical];

    public async Task<LogModel> LoadAsync(LogFilter f, CancellationToken ct = default)
    {
        var days = Ranges.Contains(f.Days) ? f.Days : 30;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-(days - 1));
        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var results = db.Results.Where(r => r.ObservedAt >= since);
        var changes = db.Changes.Where(c => c.DetectedAt >= since);
        if (!string.IsNullOrWhiteSpace(f.Check)) { results = results.Where(r => r.CheckType == f.Check); changes = changes.Where(c => c.CheckType == f.Check); }
        if (!string.IsNullOrWhiteSpace(f.Asset))
        {
            var q = f.Asset.Trim().ToLowerInvariant();
            var ids = db.Assets.Where(a => a.Name.Contains(q)).Select(a => a.Id);
            results = results.Where(r => ids.Contains(r.AssetId));
            changes = changes.Where(c => ids.Contains(c.AssetId));
        }

        // Days are grouped by their UTC year, month and day parts, which Npgsql translates; .Date on a DateTimeOffset it does not.
        var perDay = (await results.GroupBy(r => new { r.ObservedAt.UtcDateTime.Year, r.ObservedAt.UtcDateTime.Month, r.ObservedAt.UtcDateTime.Day, r.Status })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Status, N = g.Count() }).ToListAsync(ct))
            .GroupBy(x => new DateOnly(x.Year, x.Month, x.Day))
            .ToDictionary(g => g.Key, g => Order.Select(s => g.Where(x => x.Status == s).Sum(x => x.N)).ToArray());
        var changesPerDay = (await changes.GroupBy(c => new { c.DetectedAt.UtcDateTime.Year, c.DetectedAt.UtcDateTime.Month, c.DetectedAt.UtcDateTime.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => new DateOnly(x.Year, x.Month, x.Day), x => x.N);
        var byCheck = (await results.GroupBy(r => new { r.CheckType, r.Status }).Select(g => new { g.Key.CheckType, g.Key.Status, N = g.Count() }).ToListAsync(ct))
            .GroupBy(x => x.CheckType).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => Order.Select(s => g.Where(x => x.Status == s).Sum(x => x.N)).ToArray());

        var total = perDay.Values.Sum(v => v.Sum());
        var ok = perDay.Values.Sum(v => v[0]);
        var alertsInRange = db.AlertLog.Where(a => a.At >= since);

        var table = from r in results join a in db.Assets on r.AssetId equals a.Id select new { r, a.Name };
        if (f.Status is { } st) table = table.Where(x => x.r.Status == st);
        var rowCount = await table.CountAsync(ct);
        var page = Math.Clamp(f.Page, 1, Math.Max(1, (int)Math.Ceiling(rowCount / (double)PageSize)));
        var rows = (await table.OrderByDescending(x => x.r.ObservedAt).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new { x.r.ObservedAt, x.Name, x.r.AssetId, x.r.CheckType, x.r.Status, x.r.Summary }).ToListAsync(ct))
            .Select(x => new LogRow(x.ObservedAt, x.Name, x.AssetId, x.CheckType, x.Status, x.Summary)).ToList();

        return new LogModel(f with { Days = days, Page = page }, from, today, perDay, changesPerDay, byCheck, total, ok,
            await alertsInRange.CountAsync(a => a.Success, ct), await alertsInRange.CountAsync(a => !a.Success, ct),
            rows, rowCount, PageSize, await db.AlertLog.OrderByDescending(a => a.At).Take(20).ToListAsync(ct));
    }
}
