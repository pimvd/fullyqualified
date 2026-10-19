using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>What a tenant has configured for alarms, with the secrets in the clear. Never send this to the browser.</summary>
public record AlertConfig(bool Enabled, string? SlackWebhook, string? WebhookUrl, string? EmailTo, CheckStatus MinStatus, bool NotifyRecovery, bool NotifyDrift)
{
    public bool HasChannels => !string.IsNullOrWhiteSpace(SlackWebhook) || !string.IsNullOrWhiteSpace(WebhookUrl) || !string.IsNullOrWhiteSpace(EmailTo);
}

public static class AlertPolicy
{
    /// <summary>Decides whether a result is worth an alarm for this configuration.</summary>
    public static bool ShouldAlert(AlertConfig c, CheckStatus? before, CheckStatus now, int changes)
    {
        if (!c.Enabled || !c.HasChannels) return false;
        var worsened = now >= c.MinStatus && now > (before ?? CheckStatus.Ok);
        var recovered = c.NotifyRecovery && now == CheckStatus.Ok && before >= c.MinStatus;
        return worsened || recovered || (c.NotifyDrift && changes > 0);
    }
}

/// <summary>
/// Sends alarms to the channels a tenant configured: Slack, a generic webhook (PagerDuty, Opsgenie, ...) and email.
/// A tenant without its own settings falls back to the server-wide <c>Alerts:*</c> configuration, which keeps single-tenant installs working.
/// </summary>
public class AlertService(AppDbContext db, IConfiguration cfg, IHttpClientFactory http, IDataProtectionProvider dp, ILogger<AlertService> log)
{
    readonly IDataProtector protector = dp.CreateProtector("fq.alerts");

    public bool CanMail => !string.IsNullOrWhiteSpace(cfg["Alerts:Email:Host"]);

    /// <summary>The tenant's settings; the server-wide configuration when it has none. Reads across tenants because background jobs run for all of them.</summary>
    public async Task<AlertConfig> ConfigFor(Guid tenantId, CancellationToken ct = default)
    {
        var row = await db.AcrossTenants<AlertSettings>().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (row is null)
            return new AlertConfig(true, cfg["Alerts:SlackWebhook"], cfg["Alerts:Webhook"], cfg["Alerts:Email:To"], CheckStatus.Warning, true, true);
        return new AlertConfig(row.Enabled, Reveal(row.SlackWebhook), Reveal(row.WebhookUrl), row.EmailTo, row.MinStatus, row.NotifyRecovery, row.NotifyDrift);
    }

    public string? Protect(string? secret) => string.IsNullOrWhiteSpace(secret) ? null : protector.Protect(secret.Trim());

    public string? Unprotect(string stored) => Reveal(stored);

    string? Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task SendAsync(Guid tenantId, string subject, string body, CancellationToken ct)
    {
        var c = await ConfigFor(tenantId, ct);
        if (!c.Enabled) return;
        await Deliver(tenantId, "slack", subject, c.SlackWebhook, url => Post(url, new { text = $"*{subject}*\n{body}" }, ct), ct);
        await Deliver(tenantId, "webhook", subject, c.WebhookUrl, url => Post(url, new { subject, body, source = "fullyqualified" }, ct), ct);
        await Deliver(tenantId, "email", subject, c.EmailTo, to => MailAsync(to, subject, body, ct), ct);
    }

    async Task Post(string url, object payload, CancellationToken ct) =>
        (await http.CreateClient().PostAsJsonAsync(url, payload, ct)).EnsureSuccessStatusCode();

    /// <summary>Sends one message through the configured SMTP server. Throws when it fails; callers decide what that means.</summary>
    public async Task MailAsync(string to, string subject, string body, CancellationToken ct)
    {
        using var smtp = new SmtpClient(cfg["Alerts:Email:Host"], cfg.GetValue("Alerts:Email:Port", 587)) { EnableSsl = cfg.GetValue("Alerts:Email:Ssl", true) };
        if (cfg["Alerts:Email:User"] is { Length: > 0 } user)
            smtp.Credentials = new System.Net.NetworkCredential(user, cfg["Alerts:Email:Password"]);
        await smtp.SendMailAsync(new MailMessage(cfg["Alerts:Email:From"] ?? "fullyqualified@localhost", to, subject, body), ct);
    }

    async Task Deliver(Guid tenantId, string channel, string subject, string? target, Func<string, Task> send, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (channel == "email" && !CanMail) { await Record(tenantId, channel, subject, false, "No SMTP server is configured (Alerts:Email:Host)", ct); return; }
        try
        {
            await send(target);
            await Record(tenantId, channel, subject, true, null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Alert channel {Channel} failed", channel);
            await Record(tenantId, channel, subject, false, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message, ct);
        }
    }

    async Task Record(Guid tenantId, string channel, string subject, bool ok, string? error, CancellationToken ct)
    {
        db.AlertLog.Add(new AlertLogEntry { TenantId = tenantId, Channel = channel, Subject = subject, Success = ok, Error = error });
        await db.SaveChangesAsync(ct);
    }
}
