using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record AlertRuleRequest(string? CheckType, string? Key, Guid? AssetId);

/// <summary>Creates and removes the alarm rules of the signed-in admin's organization.</summary>
public class AlertRuleService(AppDbContext db)
{
    public const int MaxRules = 100;

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public async Task<IResult> Create(HttpContext ctx, AlertRuleRequest r)
    {
        if (!AlertRules.IsKnown(r.CheckType, r.Key)) return Fail(400, "invalid_rule");
        // The query filter limits assets to the organization, so another organization's asset looks like it does not exist.
        if (r.AssetId is { } asset && !await db.Assets.AnyAsync(a => a.Id == asset, ctx.RequestAborted)) return Fail(404, "not_found");
        if (await db.AlertRules.CountAsync(ctx.RequestAborted) >= MaxRules) return Fail(409, "too_many_rules");
        if (await db.AlertRules.AnyAsync(x => x.CheckType == r.CheckType && x.Key == r.Key && x.AssetId == r.AssetId, ctx.RequestAborted)) return Fail(409, "rule_exists");

        var rule = new AlertRule { TenantId = Roles.TenantId(ctx.User)!.Value, CheckType = r.CheckType!, Key = r.Key!, AssetId = r.AssetId };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.Ok(new { id = rule.Id });
    }

    public async Task<IResult> Delete(HttpContext ctx, Guid id)
    {
        var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
        if (rule is null) return Fail(404, "not_found");
        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }
}
