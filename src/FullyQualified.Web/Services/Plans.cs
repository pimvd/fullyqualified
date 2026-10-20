using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>What a tenant may use. A null number means unlimited.</summary>
public record PlanLimits(int? MaxDomains, int? MaxEndpoints, int? MaxUsers, bool ReportsEnabled, int? RetentionMonths)
{
    public static PlanLimits Of(Tier t) => new(t.MaxDomains, t.MaxEndpoints, t.MaxUsers, t.ReportsEnabled, t.RetentionMonths);

    /// <summary>The tier's value unless the tenant overrides it; -1 overrides to unlimited.</summary>
    public static PlanLimits Resolve(Tier tier, Tenant t) => new(
        Pick(t.OverrideMaxDomains, tier.MaxDomains), Pick(t.OverrideMaxEndpoints, tier.MaxEndpoints), Pick(t.OverrideMaxUsers, tier.MaxUsers),
        t.OverrideReports ?? tier.ReportsEnabled, Pick(t.OverrideRetentionMonths, tier.RetentionMonths));

    static int? Pick(int? tenantValue, int? tierValue) => tenantValue is null ? tierValue : tenantValue == -1 ? null : tenantValue;
}

public record PlanUsage(int Domains, int Endpoints, int Users, int PendingInvites);

public record Plan(Tenant Tenant, Tier Tier, PlanLimits Limits, PlanUsage Usage)
{
    public bool Overridden => Tenant.OverrideMaxDomains is not null || Tenant.OverrideMaxEndpoints is not null || Tenant.OverrideMaxUsers is not null
        || Tenant.OverrideReports is not null || Tenant.OverrideRetentionMonths is not null;

    /// <summary>Null when there is room; otherwise the resource key of the limit that is reached.</summary>
    public string? Blocks(AssetKind kind) => kind switch
    {
        AssetKind.Domain when Limits.MaxDomains is { } m && Usage.Domains >= m => "Limit_Domains",
        AssetKind.Endpoint when Limits.MaxEndpoints is { } m && Usage.Endpoints >= m => "Limit_Endpoints",
        _ => null,
    };

    /// <summary>Invitations that are still open count, so a plan cannot be exceeded by inviting first and joining later.</summary>
    public bool HasRoomForUser(bool redeemingInvite = false) =>
        Limits.MaxUsers is not { } m || Usage.Users + (redeemingInvite ? 0 : Usage.PendingInvites) < m;

    public int? EndpointsLeft => Limits.MaxEndpoints is { } m ? Math.Max(0, m - Usage.Endpoints) : null;
}

/// <summary>Resolves a tenant's plan and usage. It reads across tenants because background jobs and the platform screens ask about any tenant.</summary>
public class PlanService(AppDbContext db)
{
    public async Task<Plan?> ForAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return null;
        var tier = await db.Tiers.AsNoTracking().SingleAsync(t => t.Key == tenant.TierKey, ct);
        return new Plan(tenant, tier, PlanLimits.Resolve(tier, tenant), await UsageAsync(tenantId, ct));
    }

    public async Task<PlanUsage> UsageAsync(Guid tenantId, CancellationToken ct = default)
    {
        var assets = await db.AcrossTenants<Asset>().Where(a => a.TenantId == tenantId).GroupBy(a => a.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return new PlanUsage(
            assets.Where(a => a.Key == AssetKind.Domain).Sum(a => a.N), assets.Where(a => a.Key != AssetKind.Domain).Sum(a => a.N),
            await db.AcrossTenants<User>().CountAsync(u => u.TenantId == tenantId, ct),
            await db.AcrossTenants<Invite>().CountAsync(i => i.TenantId == tenantId && i.RecoveryForUser == null && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow, ct));
    }
}
