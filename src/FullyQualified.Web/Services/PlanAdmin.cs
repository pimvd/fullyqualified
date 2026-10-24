using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>Limits as typed in a form: blank, "unlimited" or a number.</summary>
public record TierRequest(string? MaxDomains, string? MaxEndpoints, string? MaxUsers, bool ReportsEnabled, string? RetentionMonths, string? ScanIntervalMinutes = null, string? ManualCooldownMinutes = null);
/// <summary>Per-tenant overrides as typed: blank follows the tier, "unlimited" lifts the limit, a number sets it. Reports: blank, "yes" or "no".</summary>
public record PlanRequest(string? Tier, string? MaxDomains, string? MaxEndpoints, string? MaxUsers, string? Reports, string? RetentionMonths, string? ScanIntervalMinutes = null, string? ManualCooldownMinutes = null);
public record DeleteRequest(string? ConfirmName);

public class PlanAdminService(AppDbContext db, Session session, ILogger<PlanAdminService> log)
{
    const int MaxLimit = 1_000_000, MaxRetentionMonths = 120, MaxIntervalMinutes = 7 * 24 * 60, MaxCooldownMinutes = 24 * 60;

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    static bool Unlimited(string? v) => v?.Trim().ToLowerInvariant() is "unlimited" or "∞" or "-1" or "onbeperkt" or "default" or "standaard";

    /// <summary>Parses a limit. Blank is null; "unlimited" is null for a tier and -1 for an override. False when invalid.</summary>
    static bool TryLimit(string? text, int max, bool asOverride, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true; // blank: unlimited for a tier, "follow the tier" for an override, both stored as null
        if (Unlimited(text)) { value = asOverride ? -1 : null; return true; }
        if (!int.TryParse(text.Trim(), out var n) || n < 0 || n > max) return false;
        value = n;
        return true;
    }

    public async Task<IResult> SaveTier(string key, TierRequest r)
    {
        var tier = await db.Tiers.SingleOrDefaultAsync(t => t.Key == key);
        if (tier is null) return Fail(404, "not_found");
        if (!TryLimit(r.MaxDomains, MaxLimit, false, out var domains) || !TryLimit(r.MaxEndpoints, MaxLimit, false, out var endpoints)
            || !TryLimit(r.MaxUsers, MaxLimit, false, out var users) || !TryLimit(r.RetentionMonths, MaxRetentionMonths, false, out var months)
            || !TryLimit(r.ScanIntervalMinutes, MaxIntervalMinutes, false, out var interval) || !TryLimit(r.ManualCooldownMinutes, MaxCooldownMinutes, false, out var cooldown)
            || months == 0 || interval == 0) return Fail(400, "invalid_limit");
        (tier.MaxDomains, tier.MaxEndpoints, tier.MaxUsers, tier.ReportsEnabled, tier.RetentionMonths, tier.ScanIntervalMinutes, tier.ManualCooldownMinutes, tier.UpdatedAt) =
            (domains, endpoints, users, r.ReportsEnabled, months, interval, cooldown, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    public async Task<IResult> SavePlan(Guid id, PlanRequest r)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return Fail(404, "not_found");
        var tier = (r.Tier ?? "").Trim().ToLowerInvariant();
        if (!Tiers.Keys.Contains(tier)) return Fail(400, "invalid_tier");
        if (!TryLimit(r.MaxDomains, MaxLimit, true, out var domains) || !TryLimit(r.MaxEndpoints, MaxLimit, true, out var endpoints)
            || !TryLimit(r.MaxUsers, MaxLimit, true, out var users) || !TryLimit(r.RetentionMonths, MaxRetentionMonths, true, out var months)
            || !TryLimit(r.ScanIntervalMinutes, MaxIntervalMinutes, true, out var interval) || !TryLimit(r.ManualCooldownMinutes, MaxCooldownMinutes, true, out var cooldown)
            || months == 0 || interval == 0) return Fail(400, "invalid_limit");
        var reportsText = r.Reports?.Trim().ToLowerInvariant() ?? "";
        if (reportsText is not ("" or "yes" or "no")) return Fail(400, "invalid_limit");
        bool? reports = reportsText == "" ? null : reportsText == "yes";
        (tenant.TierKey, tenant.OverrideMaxDomains, tenant.OverrideMaxEndpoints, tenant.OverrideMaxUsers, tenant.OverrideReports, tenant.OverrideRetentionMonths, tenant.OverrideScanIntervalMinutes, tenant.OverrideManualCooldownMinutes) =
            (tier, domains, endpoints, users, reports, months, interval, cooldown);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// Removes a tenant and everything it owns. A tenant's own admin may do it, and so may the management owner; the management tenant itself
    /// cannot be deleted. The caller must type the tenant's name, so a stray request cannot do it.
    /// </summary>
    public async Task<IResult> DeleteTenant(HttpContext ctx, Guid id, DeleteRequest r)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == id);
        var own = Roles.TenantId(ctx.User) == id && Roles.IsAdmin(ctx.User);
        if (tenant is null || !(own || Roles.IsPlatformAdmin(ctx.User))) return Fail(404, "not_found");
        if (tenant.IsManagement) return Fail(409, "management_tenant");
        if (!string.Equals(r.ConfirmName?.Trim(), tenant.Name, StringComparison.Ordinal)) return Fail(400, "confirm_mismatch");

        await using var tx = await db.Database.BeginTransactionAsync();
        var users = await db.AcrossTenants<User>().Where(u => u.TenantId == id).Select(u => u.Id).ToListAsync();
        // Children first. The database would cascade from assets and users, but spelling it out keeps this correct if a table is added later.
        await db.AcrossTenants<Job>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<CheckResult>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<Change>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<CertificateRecord>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<Asset>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<AlertLogEntry>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<AlertSettings>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<Invite>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<ExternalLogin>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<Passkey>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.AcrossTenants<User>().Where(x => x.TenantId == id).ExecuteDeleteAsync();
        await db.Tenants.Where(t => t.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();

        foreach (var u in users) session.Forget(u);
        log.LogWarning("Tenant {Tenant} ({Id}) with {Users} users was deleted by {By}", tenant.Name, id, users.Count, ctx.User.Identity?.Name);
        if (own) await ctx.SignOutAsync(Session.Scheme);
        return Results.NoContent();
    }
}

public static class PlanEndpoints
{
    public static void MapPlans(this WebApplication app)
    {
        // Members and admins can ask for every check of their tenant to run now.
        app.MapPost("/checks/run", async (HttpContext c, JobQueue queue) =>
        {
            var r = await queue.EnqueueAllAsync(Roles.TenantId(c.User)!.Value, DateTimeOffset.UtcNow);
            return r.Wait is { } wait
                ? Results.Json(new { error = "cooldown", retryAfterSeconds = (int)Math.Ceiling(wait.TotalSeconds) }, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Ok(new { queued = r.Queued });
        }).RequireAuthorization(Roles.Edit);

        var platform = app.MapGroup("/platform").RequireAuthorization(Roles.Platform);
        platform.MapPut("/tiers/{key}", (PlanAdminService s, string key, TierRequest r) => s.SaveTier(key, r));
        platform.MapPut("/tenants/{id:guid}/plan", (PlanAdminService s, Guid id, PlanRequest r) => s.SavePlan(id, r));
        platform.MapPost("/tenants/{id:guid}/delete", (HttpContext c, PlanAdminService s, Guid id, DeleteRequest r) => s.DeleteTenant(c, id, r));

        // A tenant's own admin deletes their organization through the same code path.
        app.MapPost("/admin/tenant/delete", (HttpContext c, PlanAdminService s, DeleteRequest r) => s.DeleteTenant(c, Roles.TenantId(c.User)!.Value, r))
            .RequireAuthorization(Roles.Admin);
    }
}
