using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>The review of hostnames found in the certificate logs: search, add, ignore with a reason, restore.</summary>
public static class ReviewHosts
{
    public const int MaxReason = 200;

    static string? Clean(string? reason) => reason?.Trim() is { Length: > 0 } r ? (r.Length > MaxReason ? r[..MaxReason] : r) : null;

    /// <summary>Only a path on this site: never somewhere else.</summary>
    static string Back(string? back, string fallback) => back is { Length: > 0 } b && b.StartsWith('/') && !b.StartsWith("//") && !b.Contains('\\') ? b : fallback;

    static List<Guid> Ids(HttpContext ctx) => ctx.Request.Form["ids"].Select(v => Guid.TryParse(v, out var g) ? g : (Guid?)null)
        .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();

    /// <summary>
    /// A discovered asset that is deleted goes on the ignored list, so the next search does not bring it back.
    /// Call before removing the assets.
    /// </summary>
    public static async Task IgnoreRemovedAsync(AppDbContext db, IEnumerable<Asset> assets, string? by)
    {
        var removed = assets.Where(a => a.Discovered).ToList();
        if (removed.Count == 0) return;
        var names = removed.Select(a => a.Name).ToList();
        var filed = await db.ReviewHosts.Where(h => names.Contains(h.Name)).ToDictionaryAsync(h => h.Name);
        var now = DateTimeOffset.UtcNow;
        foreach (var a in removed)
        {
            if (!filed.TryGetValue(a.Name, out var host))
                db.ReviewHosts.Add(host = new ReviewHost { TenantId = a.TenantId, Name = a.Name, ParentAssetId = a.DiscoveredFromId, Service = a.DiscoveredVia, Issuer = a.DiscoveredIssuer, CertExpires = a.DiscoveredCertExpires, FirstSeenAt = a.CreatedAt });
            host.State = ReviewState.Ignored; host.Reason = ReviewHost.RemovedReason; host.DecidedAt = now; host.DecidedBy = by;
        }
    }

    public static void MapReview(this WebApplication app)
    {
        var g = app.MapGroup("").RequireAuthorization(Roles.Edit);

        // One domain: look it up in the certificate logs now.
        g.MapPost("/assets/{id:guid}/discover", async (Guid id, AppDbContext db, JobQueue queue, [FromForm] string? back) =>
        {
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id);
            if (asset is null) return Results.NotFound();
            await queue.EnqueueManualAsync(asset, "ct");
            return Results.Redirect(Back(back, $"/assets/{id}"));
        });

        // Every domain of the tenant.
        g.MapPost("/review/discover", async (HttpContext ctx, AppDbContext db, JobQueue queue, [FromForm] string? back) =>
        {
            var queued = 0;
            foreach (var asset in await db.Assets.Where(a => !a.Discovered && !a.Name.Contains(":")).ToListAsync())
                if (await queue.EnqueueManualAsync(asset, "ct")) queued++;
            return Results.Redirect($"{Back(back, "/review")}{(Back(back, "/review").Contains('?') ? "&" : "?")}done=discover:{queued}");
        });

        g.MapPost("/review/add", async (HttpContext ctx, AppDbContext db, JobQueue queue, PlanService plans, [FromForm] string? back) =>
        {
            var ids = Ids(ctx);
            var tenant = Roles.TenantId(ctx.User)!.Value;
            var who = ctx.User.Identity?.Name;
            var hosts = ids.Count == 0 ? [] : await db.ReviewHosts.Where(h => ids.Contains(h.Id) && h.State != ReviewState.Added).ToListAsync();
            int added = 0, blocked = 0;
            foreach (var host in hosts)
            {
                var now = DateTimeOffset.UtcNow;
                if (!await db.Assets.AnyAsync(a => a.Name == host.Name))
                {
                    // Hostnames from the logs are endpoints, so they stop at the plan's limit like anything added by hand.
                    if (await plans.ForAsync(tenant) is { } plan && plan.Blocks(AssetKind.Endpoint) is not null) { blocked++; continue; }
                    var asset = new Asset
                    {
                        Kind = AssetKind.Endpoint, Name = host.Name, Discovered = true, DiscoveredVia = host.Service, DiscoveredFromId = host.ParentAssetId,
                        DiscoveredIssuer = host.Issuer, DiscoveredCertExpires = host.CertExpires,
                    };
                    db.Assets.Add(asset);
                    host.State = ReviewState.Added; host.Reason = null; host.DecidedAt = now; host.DecidedBy = who;
                    await db.SaveChangesAsync();
                    await queue.EnqueueAsync(asset);
                    added++;
                }
                else
                {
                    host.State = ReviewState.Added; host.Reason = null; host.DecidedAt = now; host.DecidedBy = who; // it is monitored already
                    await db.SaveChangesAsync();
                }
            }
            var target = Back(back, "/review");
            var sep = target.Contains('?') ? "&" : "?";
            return Results.Redirect(ids.Count == 0 ? $"{target}{sep}done=none" : $"{target}{sep}done=add:{added}:{blocked}");
        });

        g.MapPost("/review/ignore", async (HttpContext ctx, AppDbContext db, [FromForm] string? reason, [FromForm] string? back) =>
        {
            var target = Back(back, "/review");
            var sep = target.Contains('?') ? "&" : "?";
            var ids = Ids(ctx);
            if (ids.Count == 0) return Results.Redirect($"{target}{sep}done=none");
            if (Clean(reason) is not { } why) return Results.Redirect($"{target}{sep}error=reason");   // an ignored hostname always says why
            var hosts = await db.ReviewHosts.Where(h => ids.Contains(h.Id) && h.State == ReviewState.Pending).ToListAsync();
            foreach (var host in hosts) { host.State = ReviewState.Ignored; host.Reason = why; host.DecidedAt = DateTimeOffset.UtcNow; host.DecidedBy = ctx.User.Identity?.Name; }
            await db.SaveChangesAsync();
            return Results.Redirect($"{target}{sep}done=ignore:{hosts.Count}");
        });

        g.MapPost("/review/restore", async (HttpContext ctx, AppDbContext db, [FromForm] string? back) =>
        {
            var target = Back(back, "/review");
            var sep = target.Contains('?') ? "&" : "?";
            var ids = Ids(ctx);
            if (ids.Count == 0) return Results.Redirect($"{target}{sep}done=none");
            var hosts = await db.ReviewHosts.Where(h => ids.Contains(h.Id) && h.State == ReviewState.Ignored).ToListAsync();
            foreach (var host in hosts) { host.State = ReviewState.Pending; host.Reason = null; host.DecidedAt = null; host.DecidedBy = null; }
            await db.SaveChangesAsync();
            return Results.Redirect($"{target}{sep}done=restore:{hosts.Count}");
        });
    }
}
