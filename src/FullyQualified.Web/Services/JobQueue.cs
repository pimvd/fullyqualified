using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <param name="Queued">Checks put on the queue now.</param>
/// <param name="Wait">Set when the cooldown is still running; nothing was queued.</param>
public record CheckAllResult(int Queued, TimeSpan? Wait);

/// <summary>Job table in Postgres; workers claim rows with FOR UPDATE SKIP LOCKED.</summary>
public class JobQueue(AppDbContext db, IEnumerable<ICheck> checks, IConfiguration cfg)
{
    const string ClaimSql = """
        UPDATE jobs SET "State" = 'running', "LockedAt" = now()
        WHERE "Id" = (SELECT "Id" FROM jobs WHERE "State" = 'pending' AND "RunAt" <= now()
                      ORDER BY "RunAt" FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING *
        """;

    public async Task EnqueueAsync(Asset asset, CancellationToken ct = default)
    {
        if (asset.IsPaused(DateTimeOffset.UtcNow)) return; // paused assets are not checked, whoever asks
        var existing = await db.Jobs.Where(j => j.AssetId == asset.Id).Select(j => j.CheckType).ToListAsync(ct);
        foreach (var check in checks.Where(c => c.Automatic && c.AppliesTo(asset) && !existing.Contains(c.Type)))
            db.Jobs.Add(new Job { TenantId = asset.TenantId, AssetId = asset.Id, CheckType = check.Type });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues one check that does not run by itself, such as the certificate-log search. False when it cannot run now: paused, not applicable or already queued.</summary>
    public async Task<bool> EnqueueManualAsync(Asset asset, string type, CancellationToken ct = default)
    {
        if (asset.IsPaused(DateTimeOffset.UtcNow)) return false;
        var check = checks.FirstOrDefault(c => c.Type == type && c.AppliesTo(asset));
        if (check is null || await db.Jobs.AnyAsync(j => j.AssetId == asset.Id && j.CheckType == type, ct)) return false;
        db.Jobs.Add(new Job { TenantId = asset.TenantId, AssetId = asset.Id, CheckType = type });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// "Check everything now": queues every check of every asset of a tenant, whatever the plan's interval says. To keep this from
    /// becoming a way around the interval, a tenant waits between uses: the cooldown of its plan, or <c>Scanning:ManualCooldownMinutes</c> (default 5)
    /// when the plan has none.
    /// </summary>
    public async Task<CheckAllResult> EnqueueAllAsync(Guid tenantId, DateTimeOffset now, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var tier = await db.Tiers.AsNoTracking().SingleAsync(t => t.Key == tenant.TierKey, ct);
        var cooldown = TimeSpan.FromMinutes(PlanLimits.Resolve(tier, tenant).ManualCooldownMinutes ?? cfg.GetValue("Scanning:ManualCooldownMinutes", 5.0));
        if (tenant.LastManualRunAt is { } last && now - last < cooldown) return new(0, cooldown - (now - last));

        var assets = await db.AcrossTenants<Asset>().Where(a => a.TenantId == tenantId).ToListAsync(ct);
        var queued = (await db.AcrossTenants<Job>().Where(j => j.TenantId == tenantId).Select(j => new { j.AssetId, j.CheckType }).ToListAsync(ct))
            .Select(j => (j.AssetId, j.CheckType)).ToHashSet();
        var added = 0;
        foreach (var asset in assets.Where(a => !a.IsPaused(now)))
            foreach (var check in checks.Where(c => c.Automatic && c.AppliesTo(asset) && !queued.Contains((asset.Id, c.Type))))
            {
                db.Jobs.Add(new Job { TenantId = tenantId, AssetId = asset.Id, CheckType = check.Type, RunAt = now });
                added++;
            }
        tenant.LastManualRunAt = now;
        await db.SaveChangesAsync(ct);
        return new(added, null);
    }

    /// <summary>Queues a check for every asset whose last result is older than the scan interval.</summary>
    public async Task EnqueueDueAsync(CancellationToken ct)
    {
        var serverDefault = TimeSpan.FromHours(cfg.GetValue("Scanning:IntervalHours", 6.0));
        var interval = await IntervalsAsync(serverDefault, ct);
        var assets = await db.Assets.ToListAsync(ct);
        var last = (await db.LatestResultsAsync()).GroupBy(r => (r.AssetId, r.CheckType))
            .ToDictionary(g => g.Key, g => g.First());
        var queued = (await db.Jobs.Select(j => new { j.AssetId, j.CheckType }).ToListAsync(ct))
            .Select(j => (j.AssetId, j.CheckType)).ToHashSet();

        foreach (var asset in assets.Where(a => !a.IsPaused(DateTimeOffset.UtcNow)))
            foreach (var check in checks.Where(c => c.Automatic && c.AppliesTo(asset)))
            {
                var key = (asset.Id, check.Type);
                if (queued.Contains(key)) continue;
                // A failed check is retried sooner than a healthy one is rescanned.
                if (last.TryGetValue(key, out var prev)
                    && DateTimeOffset.UtcNow - prev.ObservedAt < (prev.Status == CheckStatus.Error ? TimeSpan.FromMinutes(15) : interval.GetValueOrDefault(asset.TenantId, serverDefault))) continue;
                db.Jobs.Add(new Job { TenantId = asset.TenantId, AssetId = asset.Id, CheckType = check.Type });
            }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>How long each tenant's results stay fresh: its plan's interval, or the server default when the plan has none.</summary>
    async Task<Dictionary<Guid, TimeSpan>> IntervalsAsync(TimeSpan serverDefault, CancellationToken ct)
    {
        var tiers = await db.Tiers.AsNoTracking().ToDictionaryAsync(t => t.Key, ct);
        return (await db.Tenants.AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id,
            t => PlanLimits.Resolve(tiers[t.TierKey], t).ScanIntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : serverDefault);
    }

    public async Task<Job?> ClaimAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE jobs SET "State" = 'pending', "LockedAt" = NULL
            WHERE "State" = 'running' AND "LockedAt" < now() - interval '10 minutes'
            """, ct);
        return (await db.Jobs.FromSqlRaw(ClaimSql).IgnoreQueryFilters().ToListAsync(ct)).FirstOrDefault();
    }
}
