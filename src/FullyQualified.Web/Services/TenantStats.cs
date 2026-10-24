using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record TenantStat(Tenant Tenant, int Users, DateTimeOffset? LastLogin, int Assets, int Domains, int Endpoints,
    int Certificates, int Expiring, int Problems, DateTimeOffset? LastScan, int Changes7d);

/// <summary>Per-tenant numbers for the platform screens. These read across tenants on purpose, so only call them for platform admins.</summary>
public class TenantStats(AppDbContext db)
{
    public const int ExpiringDays = 30;

    public async Task<List<TenantStat>> ForAsync(Guid? only = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tenants = await db.Tenants.Where(t => only == null || t.Id == only).OrderByDescending(t => t.IsManagement).ThenBy(t => t.Name).ToListAsync(ct);

        var users = (await db.AcrossTenants<User>().GroupBy(u => u.TenantId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(u => u.LastLoginAt) }).ToListAsync(ct)).ToDictionary(x => x.Key);
        var assets = (await db.AcrossTenants<Asset>().GroupBy(a => a.TenantId)
            .Select(g => new { g.Key, Count = g.Count(), Domains = g.Count(a => !a.Discovered && !a.Name.Contains(":")) }).ToListAsync(ct)).ToDictionary(x => x.Key);
        var certs = (await db.AcrossTenants<CertificateRecord>().Where(c => c.Source == "endpoint").GroupBy(c => c.TenantId)
            .Select(g => new { g.Key, Count = g.Count(), Expiring = g.Count(c => c.NotAfter <= now.AddDays(ExpiringDays)) }).ToListAsync(ct)).ToDictionary(x => x.Key);
        var changes = (await db.AcrossTenants<Change>().Where(c => c.DetectedAt >= now.AddDays(-7)).GroupBy(c => c.TenantId)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Count);

        var results = await db.AcrossTenants<CheckResult>()
            .Where(r => r.ObservedAt == db.AcrossTenants<CheckResult>().Where(x => x.AssetId == r.AssetId && x.CheckType == r.CheckType).Max(x => x.ObservedAt))
            .Select(r => new { r.TenantId, r.AssetId, r.Status, r.ObservedAt }).ToListAsync(ct);
        var byTenant = results.GroupBy(r => r.TenantId).ToDictionary(g => g.Key);

        return tenants.Select(t =>
        {
            users.TryGetValue(t.Id, out var u); assets.TryGetValue(t.Id, out var a); certs.TryGetValue(t.Id, out var c);
            byTenant.TryGetValue(t.Id, out var r);
            var assetCount = a?.Count ?? 0;
            return new TenantStat(t, u?.Count ?? 0, u?.Last, assetCount, a?.Domains ?? 0, assetCount - (a?.Domains ?? 0),
                c?.Count ?? 0, c?.Expiring ?? 0,
                r?.Where(x => x.Status >= CheckStatus.Warning).Select(x => x.AssetId).Distinct().Count() ?? 0,
                r?.Max(x => (DateTimeOffset?)x.ObservedAt), changes.GetValueOrDefault(t.Id));
        }).ToList();
    }
}
