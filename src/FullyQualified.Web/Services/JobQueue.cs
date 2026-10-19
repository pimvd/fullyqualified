using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

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
        var existing = await db.Jobs.Where(j => j.AssetId == asset.Id).Select(j => j.CheckType).ToListAsync(ct);
        foreach (var check in checks.Where(c => c.AppliesTo(asset) && !existing.Contains(c.Type)))
            db.Jobs.Add(new Job { AssetId = asset.Id, CheckType = check.Type });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues a check for every asset whose last result is older than the scan interval.</summary>
    public async Task EnqueueDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(cfg.GetValue("Scanning:IntervalHours", 6.0));
        var assets = await db.Assets.ToListAsync(ct);
        var last = (await db.LatestResultsAsync()).GroupBy(r => (r.AssetId, r.CheckType))
            .ToDictionary(g => g.Key, g => g.First());
        var queued = (await db.Jobs.Select(j => new { j.AssetId, j.CheckType }).ToListAsync(ct))
            .Select(j => (j.AssetId, j.CheckType)).ToHashSet();

        foreach (var asset in assets)
            foreach (var check in checks.Where(c => c.AppliesTo(asset)))
            {
                var key = (asset.Id, check.Type);
                if (queued.Contains(key)) continue;
                // A failed check is retried sooner than a healthy one is rescanned.
                if (last.TryGetValue(key, out var prev)
                    && DateTimeOffset.UtcNow - prev.ObservedAt < (prev.Status == CheckStatus.Error ? TimeSpan.FromMinutes(15) : interval)) continue;
                db.Jobs.Add(new Job { AssetId = asset.Id, CheckType = check.Type });
            }
        await db.SaveChangesAsync(ct);
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
