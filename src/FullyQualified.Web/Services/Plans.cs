using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>What a tenant may use. A null number means unlimited.</summary>
/// <param name="ScanIntervalMinutes">Minutes between checks of an asset; null means the server default.</param>
/// <param name="ManualCooldownMinutes">Minutes between uses of "check everything now"; null means the server default, 0 no wait.</param>
public record PlanLimits(int? MaxAssets, int? MaxUsers, bool ReportsEnabled, int? RetentionMonths, int? ScanIntervalMinutes = null, int? ManualCooldownMinutes = null)
{
    public static PlanLimits Of(Tier t) => new(t.MaxAssets, t.MaxUsers, t.ReportsEnabled, t.RetentionMonths, t.ScanIntervalMinutes, t.ManualCooldownMinutes);

    /// <summary>The tier's value unless the tenant overrides it; -1 overrides to unlimited.</summary>
    public static PlanLimits Resolve(Tier tier, Tenant t) => new(
        Pick(t.OverrideMaxAssets, tier.MaxAssets), Pick(t.OverrideMaxUsers, tier.MaxUsers),
        t.OverrideReports ?? tier.ReportsEnabled, Pick(t.OverrideRetentionMonths, tier.RetentionMonths), Pick(t.OverrideScanIntervalMinutes, tier.ScanIntervalMinutes), Pick(t.OverrideManualCooldownMinutes, tier.ManualCooldownMinutes));

    static int? Pick(int? tenantValue, int? tierValue) => tenantValue is null ? tierValue : tenantValue == -1 ? null : tenantValue;
}

public record PlanUsage(int Assets, int Users, int PendingInvites);

public record Plan(Tenant Tenant, Tier Tier, PlanLimits Limits, PlanUsage Usage)
{
    public bool Overridden => Tenant.OverrideMaxAssets is not null || Tenant.OverrideMaxUsers is not null
        || Tenant.OverrideReports is not null || Tenant.OverrideRetentionMonths is not null;

    /// <summary>Null when there is room; otherwise the resource key of the limit that is reached.</summary>
    public string? Blocks() => Limits.MaxAssets is { } m && Usage.Assets >= m ? "Limit_Assets" : null;

    /// <summary>Invitations that are still open count, so a plan cannot be exceeded by inviting first and joining later.</summary>
    public bool HasRoomForUser(bool redeemingInvite = false) =>
        Limits.MaxUsers is not { } m || Usage.Users + (redeemingInvite ? 0 : Usage.PendingInvites) < m;

    public int? AssetsLeft => Limits.MaxAssets is { } m ? Math.Max(0, m - Usage.Assets) : null;
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
        return new PlanUsage(
            await db.AcrossTenants<Asset>().CountAsync(a => a.TenantId == tenantId, ct),
            await db.AcrossTenants<User>().CountAsync(u => u.TenantId == tenantId, ct),
            await db.AcrossTenants<Invite>().CountAsync(i => i.TenantId == tenantId && i.RecoveryForUser == null && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow, ct));
    }
}
