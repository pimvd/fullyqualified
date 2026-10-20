using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

public enum Finding { Ok = 0, Info = 1, Warning = 2, Fail = 3 }

public record TestItem(string Group, string Title, Finding Status, string Detail);

/// <summary>The few things the settings test needs from the outside world, so tests can answer for them.</summary>
public class ServerProbes
{
    public virtual async Task<string?> TcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, cts.Token);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return ex.Message; }
    }

    public virtual async Task<string?> HttpAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500 ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return ex.Message; }
    }

    public virtual async Task<string?> ResolveAsync(string name, CancellationToken ct)
    {
        try { await Dns.GetHostAddressesAsync(name, ct).WaitAsync(TimeSpan.FromSeconds(5), ct); return null; }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return ex.Message; }
    }
}

/// <summary>
/// "Test settings": looks at everything that decides whether FullyQualified will do its job for a tenant, and says what is wrong in words.
/// Admins get the checks for their organization; the platform owner also gets the checks of the server. Nothing here changes any setting.
/// Sending a test alarm is the only action, and only when asked.
/// </summary>
public class SettingsTestService(AppDbContext db, PlanService plans, AlertService alerts, IEnumerable<ICheck> checks, ServerProbes probes,
    IConfiguration cfg, IStringLocalizer<SharedResource> L)
{
    string Group(string key) => L["Diag_Group_" + key].Value;

    public async Task<List<TestItem>> RunAsync(HttpContext ctx, bool sendAlarm, CancellationToken ct)
    {
        var tenantId = Roles.TenantId(ctx.User)!.Value;
        var items = new List<TestItem>();
        items.AddRange(await PlanItems(tenantId, ct));
        items.AddRange(await ScanItems(tenantId, ct));
        items.AddRange(await AlarmItems(tenantId, sendAlarm, ct));
        items.AddRange(await PeopleItems(ct));
        if (Roles.IsPlatformAdmin(ctx.User)) items.AddRange(await ServerItems(ctx, ct));
        return items;
    }

    // ---- the organization ----

    async Task<List<TestItem>> PlanItems(Guid tenantId, CancellationToken ct)
    {
        var g = Group("plan");
        var plan = (await plans.ForAsync(tenantId, ct))!;
        var full = new List<string>();
        void Check(string name, int used, int? max) { if (max is { } m && used >= m) full.Add($"{name} {used}/{m}"); }
        Check(L["Plan_Domains"], plan.Usage.Domains, plan.Limits.MaxDomains);
        Check(L["Plan_Endpoints"], plan.Usage.Endpoints, plan.Limits.MaxEndpoints);
        Check(L["Plan_Users"], plan.Usage.Users + plan.Usage.PendingInvites, plan.Limits.MaxUsers);
        var summary = L["Diag_PlanIs", plan.Tier.Name].Value;
        return [full.Count == 0
            ? new(g, L["Diag_PlanLimits"].Value, Finding.Ok, summary + " " + L["Diag_RoomLeft"])
            : new(g, L["Diag_PlanLimits"].Value, Finding.Warning, summary + " " + L["Diag_AtLimit", string.Join(", ", full)])];
    }

    async Task<List<TestItem>> ScanItems(Guid tenantId, CancellationToken ct)
    {
        var g = Group("scan");
        var items = new List<TestItem>();
        var plan = (await plans.ForAsync(tenantId, ct))!;
        var minutes = plan.Limits.ScanIntervalMinutes ?? (int)(cfg.GetValue("Scanning:IntervalHours", 6.0) * 60);
        items.Add(new(g, L["Diag_Interval"].Value, Finding.Info, L["Diag_IntervalIs", ResourceText.Interval(L, minutes), plan.Limits.ScanIntervalMinutes is null ? L["Diag_FromServer"].Value : L["Diag_FromPlan"].Value].Value));

        if (!cfg.GetValue("Scanning:Enabled", true))
        {
            items.Add(new(g, L["Diag_ScannerOn"].Value, Finding.Fail, L["Diag_ScannerOff"].Value));
            return items;
        }

        var assets = await db.Assets.ToListAsync(ct);
        if (assets.Count == 0) { items.Add(new(g, L["Diag_Fresh"].Value, Finding.Info, L["Diag_NoAssets"].Value)); return items; }
        var latest = (await db.LatestResultsAsync()).ToDictionary(r => (r.AssetId, r.CheckType));
        var now = DateTimeOffset.UtcNow;
        var stale = new List<string>();
        foreach (var a in assets)
            foreach (var c in checks.Where(c => c.AppliesTo(a)))
            {
                // Fresh means a new result within about two intervals; a failed check is retried after 15 minutes, so it gets 20.
                if (latest.TryGetValue((a.Id, c.Type), out var r))
                {
                    var allowed = r.Status == CheckStatus.Error ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(minutes * 2 + 5);
                    if (now - r.ObservedAt > allowed) stale.Add(a.Name);
                }
                else if (now - a.CreatedAt > TimeSpan.FromMinutes(10)) stale.Add(a.Name);
            }
        var names = stale.Distinct().Take(3).ToList();
        items.Add(stale.Count == 0
            ? new(g, L["Diag_Fresh"].Value, Finding.Ok, L["Diag_FreshOk", assets.Count].Value)
            : new(g, L["Diag_Fresh"].Value, Finding.Warning, L["Diag_Stale", stale.Count, string.Join(", ", names) + (stale.Distinct().Count() > names.Count ? "…" : "")].Value));
        return items;
    }

    async Task<List<TestItem>> AlarmItems(Guid tenantId, bool sendAlarm, CancellationToken ct)
    {
        var g = Group("alarms");
        var items = new List<TestItem>();
        var config = await alerts.ConfigFor(tenantId, ct);
        var title = L["Diag_AlarmSetup"].Value;
        if (!config.HasChannels) { items.Add(new(g, title, Finding.Warning, L["Diag_NoChannel"].Value)); return items; }
        if (!config.Enabled) items.Add(new(g, title, Finding.Warning, L["Diag_AlarmsOff"].Value));
        else items.Add(new(g, title, Finding.Ok, L["Diag_AlarmsOn", L["Status_" + config.MinStatus].Value].Value));
        if (!string.IsNullOrWhiteSpace(config.EmailTo) && !alerts.CanMail)
            items.Add(new(g, L["Diag_EmailChannel"].Value, Finding.Fail, L["Diag_NoSmtp"].Value));

        if (sendAlarm && config.Enabled)
        {
            var started = DateTimeOffset.UtcNow;
            await alerts.SendAsync(tenantId, "FullyQualified: test alarm", "This is a test alarm from the settings test.", ct);
            foreach (var e in await db.AlertLog.Where(a => a.At >= started).OrderBy(a => a.At).ToListAsync(ct))
                items.Add(new(g, L["Diag_Channel_" + e.Channel].Value, e.Success ? Finding.Ok : Finding.Fail, e.Success ? L["Diag_Delivered"].Value : L["Diag_NotDelivered", e.Error ?? ""].Value));
        }
        else if (sendAlarm) items.Add(new(g, L["Diag_TestAlarm"].Value, Finding.Info, L["Diag_NotSent"].Value));

        var week = DateTimeOffset.UtcNow.AddDays(-7);
        var recent = await db.AlertLog.Where(a => a.At >= week && a.Subject != "FullyQualified: test alarm").Select(a => a.Success).ToListAsync(ct);
        if (recent.Count(s => !s) is > 0 and var failed)
            items.Add(new(g, L["Diag_RecentAlarms"].Value, Finding.Warning, L["Diag_RecentFailed", failed, recent.Count].Value));
        return items;
    }

    async Task<List<TestItem>> PeopleItems(CancellationToken ct)
    {
        var g = Group("people");
        var admins = await db.Users.Where(u => u.Role == Role.Admin).Select(u => u.Id).ToListAsync(ct);
        var passkeys = await db.Passkeys.Where(p => admins.Contains(p.UserId)).GroupBy(p => p.UserId).ToDictionaryAsync(x => x.Key, x => x.Count(), ct);
        var sso = (await db.ExternalLogins.Where(x => admins.Contains(x.UserId)).Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        var items = new List<TestItem>
        {
            admins.Count >= 2 ? new(g, L["Diag_Admins"].Value, Finding.Ok, L["Diag_AdminsOk", admins.Count].Value)
                              : new(g, L["Diag_Admins"].Value, Finding.Warning, L["Diag_OneAdmin"].Value),
        };
        var single = admins.Count(id => passkeys.GetValueOrDefault(id) < 2 && !sso.Contains(id));
        items.Add(single == 0 ? new(g, L["Diag_Passkeys"].Value, Finding.Ok, L["Diag_PasskeysOk"].Value)
                              : new(g, L["Diag_Passkeys"].Value, Finding.Warning, L["Diag_PasskeysFew", single].Value));
        return items;
    }

    // ---- the server (platform owner only) ----

    async Task<List<TestItem>> ServerItems(HttpContext ctx, CancellationToken ct)
    {
        var g = Group("server");
        var items = new List<TestItem>();
        var req = ctx.Request;

        var local = req.Host.Host is "localhost" or "127.0.0.1" or "[::1]";
        var configured = cfg["Auth:Origin"];
        var seen = $"{req.Scheme}://{req.Host}";
        items.Add(!req.IsHttps && !local ? new(g, L["Diag_Https"].Value, Finding.Fail, L["Diag_NotHttps", seen].Value)
            : new(g, L["Diag_Https"].Value, Finding.Ok, L["Diag_HttpsOk", seen].Value));
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured.TrimEnd('/'), seen, StringComparison.OrdinalIgnoreCase))
            items.Add(new(g, L["Diag_Origin"].Value, Finding.Fail, L["Diag_OriginDiffers", configured, seen].Value));
        var behindProxy = req.Headers.ContainsKey("X-Forwarded-For") || req.Headers.ContainsKey("X-Forwarded-Proto");
        var remote = ctx.Connection.RemoteIpAddress;
        if (behindProxy && remote is not null && (IPAddress.IsLoopback(remote) || remote.ToString().StartsWith("10.") || remote.ToString().StartsWith("192.168.")) && cfg["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] is not "true")
            items.Add(new(g, L["Diag_Proxy"].Value, Finding.Warning, L["Diag_ProxyHeaders"].Value));

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        items.Add(pending.Count == 0 ? new(g, L["Diag_Migrations"].Value, Finding.Ok, L["Diag_MigrationsOk"].Value)
                                     : new(g, L["Diag_Migrations"].Value, Finding.Fail, L["Diag_MigrationsPending", pending.Count].Value));
        var keys = await db.DataProtectionKeys.CountAsync(ct);
        items.Add(keys > 0 ? new(g, L["Diag_Keys"].Value, Finding.Ok, L["Diag_KeysOk"].Value) : new(g, L["Diag_Keys"].Value, Finding.Warning, L["Diag_KeysNone"].Value));

        if (cfg["Alerts:Email:Host"] is { Length: > 0 } smtp)
        {
            var port = cfg.GetValue("Alerts:Email:Port", 587);
            var error = await probes.TcpAsync(smtp, port, ct);
            items.Add(error is null ? new(g, L["Diag_Smtp"].Value, Finding.Ok, L["Diag_SmtpOk", $"{smtp}:{port}"].Value) : new(g, L["Diag_Smtp"].Value, Finding.Fail, L["Diag_SmtpDown", $"{smtp}:{port}", error].Value));
        }
        else items.Add(new(g, L["Diag_Smtp"].Value, Finding.Info, L["Diag_NoSmtpHost"].Value));

        if (cfg["Sso:Authority"] is { Length: > 0 } authority)
        {
            var error = await probes.HttpAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", ct);
            items.Add(error is null ? new(g, L["Diag_Sso"].Value, Finding.Ok, L["Diag_SsoOk", authority].Value) : new(g, L["Diag_Sso"].Value, Finding.Fail, L["Diag_SsoDown", authority, error].Value));
            if (cfg.GetValue("Sso:AutoProvision", false) && string.IsNullOrWhiteSpace(cfg["Sso:AllowedDomains"]))
                items.Add(new(g, L["Diag_SsoOpen"].Value, Finding.Warning, L["Diag_SsoOpenText"].Value));
        }
        else items.Add(new(g, L["Diag_Sso"].Value, Finding.Info, L["Diag_NoSso"].Value));

        var dnsError = await probes.ResolveAsync("example.com", ct);
        items.Add(dnsError is null ? new(g, L["Diag_Resolver"].Value, Finding.Ok, L["Diag_ResolverOk"].Value) : new(g, L["Diag_Resolver"].Value, Finding.Fail, L["Diag_ResolverDown", dnsError].Value));
        var ctError = await probes.HttpAsync("https://crt.sh/", ct);
        items.Add(ctError is null ? new(g, L["Diag_Ct"].Value, Finding.Ok, L["Diag_CtOk"].Value) : new(g, L["Diag_Ct"].Value, Finding.Warning, L["Diag_CtDown", ctError].Value));

        items.Add(new(g, L["Diag_PrivateScans"].Value, Finding.Info, cfg.GetValue("Scanning:AllowPrivateAddresses", true) ? L["Diag_PrivateAllowed"].Value : L["Diag_PrivateBlocked"].Value));
        items.Add(new(g, L["Diag_PublicCheck"].Value, Finding.Info, cfg.GetValue("PublicScan:Enabled", false) ? L["Diag_PublicOn", cfg.GetValue("PublicScan:PerHour", 10)].Value : L["Diag_PublicOff"].Value));
        return items;
    }
}

public static class DiagnosticsEndpoints
{
    public record Request(bool SendAlarm);

    public static void MapDiagnostics(this WebApplication app) =>
        app.MapPost("/admin/diagnostics", async (HttpContext c, SettingsTestService s, Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l, Request r) =>
        {
            var items = await s.RunAsync(c, r.SendAlarm, c.RequestAborted);
            return Results.Ok(new
            {
                items = items.Select(i => new { group = i.Group, title = i.Title, status = i.Status.ToString().ToLowerInvariant(), label = l["Diag_Status_" + i.Status].Value, detail = i.Detail }),
                summary = l["Diag_Summary", items.Count(i => i.Status == Finding.Ok), items.Count(i => i.Status == Finding.Warning), items.Count(i => i.Status == Finding.Fail)].Value,
            });
        }).RequireAuthorization(Roles.Admin);
}
