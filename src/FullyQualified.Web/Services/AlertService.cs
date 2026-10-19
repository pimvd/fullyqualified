using System.Net.Mail;
using System.Net.Http.Json;

namespace FullyQualified.Web;

/// <summary>Sends alerts to every configured channel: Slack, a generic webhook (PagerDuty, Opsgenie, ...) and email.</summary>
public class AlertService(IConfiguration cfg, IHttpClientFactory http, ILogger<AlertService> log)
{
    public bool HasChannels => new[] { "Alerts:SlackWebhook", "Alerts:Webhook", "Alerts:Email:Host" }.Any(k => !string.IsNullOrWhiteSpace(cfg[k]));

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        await Try("slack", async () =>
        {
            if (cfg["Alerts:SlackWebhook"] is { Length: > 0 } url)
                (await http.CreateClient().PostAsJsonAsync(url, new { text = $"*{subject}*\n{body}" }, ct)).EnsureSuccessStatusCode();
        });
        await Try("webhook", async () =>
        {
            if (cfg["Alerts:Webhook"] is { Length: > 0 } url)
                (await http.CreateClient().PostAsJsonAsync(url, new { subject, body, source = "fullyqualified" }, ct)).EnsureSuccessStatusCode();
        });
        await Try("email", async () =>
        {
            if (cfg["Alerts:Email:Host"] is not { Length: > 0 } host) return;
            using var smtp = new SmtpClient(host, cfg.GetValue("Alerts:Email:Port", 587)) { EnableSsl = cfg.GetValue("Alerts:Email:Ssl", true) };
            if (cfg["Alerts:Email:User"] is { Length: > 0 } user)
                smtp.Credentials = new System.Net.NetworkCredential(user, cfg["Alerts:Email:Password"]);
            await smtp.SendMailAsync(new MailMessage(cfg["Alerts:Email:From"] ?? "fullyqualified@localhost", cfg["Alerts:Email:To"] ?? "", subject, body), ct);
        });
    }

    async Task Try(string channel, Func<Task> send)
    {
        try { await send(); }
        catch (Exception ex) { log.LogWarning(ex, "Alert channel {Channel} failed", channel); }
    }
}
