using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record RetentionResult(int Results, int Changes, int AlertLog);

/// <summary>
/// Deletes history older than a tenant's retention period. It always keeps the newest result of every check and the newest
/// result that was not an error, so an asset never goes blank and change detection keeps its baseline.
/// </summary>
public class RetentionService(AppDbContext db, ILogger<RetentionService> log)
{
    public async Task<RetentionResult> RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var tiers = await db.Tiers.AsNoTracking().ToDictionaryAsync(t => t.Key, ct);
        int results = 0, changes = 0, alerts = 0;
        foreach (var tenant in await db.Tenants.AsNoTracking().ToListAsync(ct))
        {
            if (PlanLimits.Resolve(tiers[tenant.TierKey], tenant).RetentionMonths is not { } months) continue;
            var cutoff = now.AddMonths(-months);
            var id = tenant.Id;
            var all = db.AcrossTenants<CheckResult>();
            var r = await all.Where(x => x.TenantId == id && x.ObservedAt < cutoff
                    && all.Any(n => n.AssetId == x.AssetId && n.CheckType == x.CheckType && n.ObservedAt > x.ObservedAt)
                    && (x.Status == CheckStatus.Error
                        || all.Any(n => n.AssetId == x.AssetId && n.CheckType == x.CheckType && n.ObservedAt > x.ObservedAt && n.Status != CheckStatus.Error)))
                .ExecuteDeleteAsync(ct);
            var c = await db.AcrossTenants<Change>().Where(x => x.TenantId == id && x.DetectedAt < cutoff).ExecuteDeleteAsync(ct);
            var a = await db.AcrossTenants<AlertLogEntry>().Where(x => x.TenantId == id && x.At < cutoff).ExecuteDeleteAsync(ct);
            if (r + c + a > 0) log.LogInformation("Retention for {Tenant}: removed {Results} results, {Changes} changes, {Alerts} alarm entries older than {Cutoff:yyyy-MM-dd}", tenant.Name, r, c, a, cutoff);
            (results, changes, alerts) = (results + r, changes + c, alerts + a);
        }
        return new RetentionResult(results, changes, alerts);
    }
}
