using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record AlertSettingsRequest(bool Enabled, string? SlackWebhook, string? WebhookUrl, string? EmailTo, string? MinStatus,
    bool NotifyRecovery, bool NotifyDrift, bool ClearSlack, bool ClearWebhook);

/// <summary>Reads and writes the alarm settings of the signed-in admin's tenant.</summary>
public class AlertSettingsService(AppDbContext db, AlertService alerts, HostGuard guard)
{
    const int MaxRecipients = 10;

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public async Task<AlertSettings?> CurrentAsync(CancellationToken ct = default) => await db.AlertSettings.SingleOrDefaultAsync(ct);

    /// <summary>Only the host of a stored URL is ever shown again; the rest may contain a secret token.</summary>
    public static string? Describe(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    public string? Reveal(string? stored) => stored is null ? null : alerts.Unprotect(stored);

    public async Task<IResult> Save(HttpContext ctx, AlertSettingsRequest r)
    {
        if (!Enum.TryParse<CheckStatus>(r.MinStatus, out var min) || min == CheckStatus.Ok) return Fail(400, "invalid_status");
        var recipients = (r.EmailTo ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AdminService.NormalizeEmail).ToList();
        if (recipients.Any(e => string.IsNullOrEmpty(e)) || recipients.Count > MaxRecipients) return Fail(400, "invalid_email");

        foreach (var (url, code) in new[] { (r.SlackWebhook, "invalid_slack"), (r.WebhookUrl, "invalid_webhook") })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Fail(400, code);
            // A tenant must not be able to make the server call into the network it runs in.
            if (!await guard.IsAllowedAsync(uri.Host, ctx.RequestAborted)) return Fail(400, "blocked_host");
        }

        var row = await CurrentAsync(ctx.RequestAborted);
        if (row is null) { row = new AlertSettings(); db.AlertSettings.Add(row); }
        row.Enabled = r.Enabled;
        row.MinStatus = min;
        row.NotifyRecovery = r.NotifyRecovery;
        row.NotifyDrift = r.NotifyDrift;
        row.EmailTo = recipients.Count == 0 ? null : string.Join(", ", recipients);
        // A blank secret field keeps what is stored; it is replaced when filled in and removed on request.
        if (r.ClearSlack) row.SlackWebhook = null; else if (!string.IsNullOrWhiteSpace(r.SlackWebhook)) row.SlackWebhook = alerts.Protect(r.SlackWebhook);
        if (r.ClearWebhook) row.WebhookUrl = null; else if (!string.IsNullOrWhiteSpace(r.WebhookUrl)) row.WebhookUrl = alerts.Protect(r.WebhookUrl);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Sends a test message through every configured channel and reports each outcome.</summary>
    public async Task<IResult> Test(HttpContext ctx)
    {
        var tenant = Roles.TenantId(ctx.User)!.Value;
        var config = await alerts.ConfigFor(tenant, ctx.RequestAborted);
        if (!config.HasChannels) return Fail(409, "no_channels");
        var started = DateTimeOffset.UtcNow;
        await alerts.SendAsync(tenant, "FullyQualified: test alarm", "This is a test alarm. If you can read it, this channel works.", ctx.RequestAborted);
        var results = await db.AlertLog.Where(a => a.At >= started).OrderBy(a => a.At)
            .Select(a => new { a.Channel, a.Success, a.Error }).ToListAsync(ctx.RequestAborted);
        return Results.Ok(results);
    }
}

public static class AlertEndpoints
{
    public static void MapAlerts(this WebApplication app)
    {
        var g = app.MapGroup("/admin/alerts").RequireAuthorization(Roles.Admin);
        g.MapPut("/", (HttpContext c, AlertSettingsService s, AlertSettingsRequest r) => s.Save(c, r));
        g.MapPost("/test", (HttpContext c, AlertSettingsService s) => s.Test(c));
    }
}
