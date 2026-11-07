using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>The SPF tree of one asset, resolved live when the asset page asks for it. Read-only, shares the toolbox's resolver and rate limits.</summary>
public static class SpfTreeEndpoint
{
    public static void MapSpfTree(this WebApplication app) => app.MapGet("/assets/{id:guid}/spf-tree",
        async (Guid id, HttpContext ctx, AppDbContext db, SpfTreeBuilder builder, ToolLimits limits, IConfiguration config) =>
    {
        // The tenant filter on assets makes another organization's asset look like it does not exist.
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ctx.RequestAborted);
        if (asset is null) return Results.NotFound();
        if (ToolNetwork.NormalizeHost(Targets.Parse(asset.Name).Host) is not { } host) return Results.BadRequest(new { error = "invalid_host" });
        using var permit = limits.Enter(Roles.UserId(ctx.User)?.ToString() ?? ctx.User.Identity?.Name ?? "authenticated");
        if (permit is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Toolbox:RunSeconds", 60), 5, 120)));
        ctx.Response.Headers.CacheControl = "no-store";
        try { return Results.Json(await builder.BuildAsync(host, timeout.Token)); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (OperationCanceledException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
    }).RequireAuthorization();
}
