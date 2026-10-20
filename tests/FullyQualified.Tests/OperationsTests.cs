using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

/// <summary>The scanner flag is on but it has no workers, so settings that depend on it can be tested without anything being scanned.</summary>
public class PgScanFactory : PgFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Scanning:Enabled", "true");
        builder.UseSetting("Scanning:Workers", "0");
    }
}

/// <summary>Answers for the outside world: the mail server is down, crt.sh is slow, the rest works.</summary>
public class FakeProbes : ServerProbes
{
    public override Task<string?> TcpAsync(string host, int port, CancellationToken ct) => Task.FromResult<string?>("connection refused");
    public override Task<string?> HttpAsync(string url, CancellationToken ct) => Task.FromResult<string?>(url.Contains("crt.sh") ? "timeout" : null);
    public override Task<string?> ResolveAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
}

public class PgProbeFactory : PgFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Alerts:Email:Host", "smtp.invalid.test");
        builder.UseSetting("Sso:Authority", "https://idp.invalid.test");
        builder.UseSetting("Sso:ClientId", "x");
        builder.UseSetting("Sso:AutoProvision", "true");
        builder.UseSetting("Auth:Origin", "https://fq.example.org");
        builder.ConfigureServices(s => s.AddSingleton<ServerProbes, FakeProbes>());
    }
}

/// <summary>Scan intervals per plan, "check everything now" and the settings test, against a real database.</summary>
public abstract class OrgTestBase<TFactory>(TFactory factory) where TFactory : PgFactory
{
    protected readonly TFactory Factory = factory;
    static readonly SemaphoreSlim Gate = new(1, 1);
    protected Guid Owner;

    protected IServiceScope Scope() { var s = Factory.Services.CreateScope(); s.ServiceProvider.GetRequiredService<TenantScope>().All = true; return s; }

    protected async Task<Guid> EnsureOwner()
    {
        await Gate.WaitAsync();
        try
        {
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            if (await db.Tenants.SingleOrDefaultAsync(t => t.IsManagement) is { } existing)
                return Owner = (await db.AcrossTenants<User>().FirstAsync(u => u.TenantId == existing.Id)).Id;
            var tenant = new Tenant { Name = "Boss", IsManagement = true, TierKey = Tiers.Custom };
            db.Tenants.Add(tenant);
            var owner = new User { TenantId = tenant.Id, Name = "Owner", Role = Role.Admin };
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            return Owner = owner.Id;
        }
        finally { Gate.Release(); }
    }

    protected record Org(Guid Id, string Name, Guid Admin, Guid Viewer);

    protected async Task<Org> NewOrg(string tier = Tiers.Starter, int admins = 1, Action<Tenant>? tweak = null)
    {
        await EnsureOwner();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..8], TierKey = tier };
        tweak?.Invoke(t);
        db.Tenants.Add(t);
        var first = new User { TenantId = t.Id, Name = "Admin", Role = Role.Admin, Email = $"a@{t.Id:N}.test" };
        var viewer = new User { TenantId = t.Id, Name = "Viewer", Role = Role.Viewer };
        db.Users.AddRange(first, viewer);
        for (var i = 1; i < admins; i++) db.Users.Add(new User { TenantId = t.Id, Name = $"Admin {i + 1}", Role = Role.Admin });
        await db.SaveChangesAsync();
        return new Org(t.Id, t.Name, first.Id, viewer.Id);
    }

    /// <summary>Adds an asset; with an age it also gets a result of that age for every check that applies to it (or just the named one).</summary>
    protected async Task<Asset> AddAsset(Guid tenant, string name, AssetKind kind = AssetKind.Domain, int? resultAgeMinutes = null, CheckStatus status = CheckStatus.Ok, string? check = null, DateTimeOffset? created = null)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = new Asset { TenantId = tenant, Name = name, Kind = kind, CreatedAt = created ?? DateTimeOffset.UtcNow.AddDays(-1) };
        db.Assets.Add(a);
        if (resultAgeMinutes is { } age)
            foreach (var type in check is null ? scope.ServiceProvider.GetServices<ICheck>().Where(c => c.AppliesTo(a)).Select(c => c.Type) : [check])
                db.Results.Add(new CheckResult { TenantId = tenant, AssetId = a.Id, CheckType = type, Status = status, Summary = "Sum_DnsOk", ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-age) });
        await db.SaveChangesAsync();
        return a;
    }

    protected int ChecksFor(Asset a) { using var s = Scope(); return s.ServiceProvider.GetServices<ICheck>().Count(c => c.AppliesTo(a)); }

    protected async Task<List<Job>> JobsOf(Guid tenant)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<Job>().Where(j => j.TenantId == tenant).ToListAsync();
    }

    protected async Task Edit(Guid tenant, Action<Tenant> change)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(await db.Tenants.SingleAsync(x => x.Id == tenant));
        await db.SaveChangesAsync();
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
}

public class ScheduleTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    // ---- the interval follows the plan ----

    [DbFact]
    public async Task The_scheduler_uses_each_tenants_plan_interval()
    {
        var fast = await NewOrg(Tiers.Business);                                           // hourly
        var slow = await NewOrg(Tiers.Free);                                               // daily
        var custom = await NewOrg(Tiers.Custom);                                           // the server default, six hours
        var overridden = await NewOrg(Tiers.Free, tweak: t => t.OverrideScanIntervalMinutes = 5);
        foreach (var o in new[] { fast, slow, custom, overridden }) await AddAsset(o.Id, $"a.{o.Id:N}.test.com", AssetKind.Endpoint, resultAgeMinutes: 90);

        using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(default);

        Assert.NotEmpty(await JobsOf(fast.Id));         // 90 minutes old, due after 60
        Assert.Empty(await JobsOf(slow.Id));            // due after 24 hours
        Assert.Empty(await JobsOf(custom.Id));          // due after 6 hours (server default)
        Assert.NotEmpty(await JobsOf(overridden.Id));   // the override of 5 minutes beats the Free plan
    }

    [DbFact]
    public async Task A_failed_check_is_still_retried_after_fifteen_minutes_whatever_the_interval()
    {
        var org = await NewOrg(Tiers.Free);
        await AddAsset(org.Id, $"err.{org.Id:N}.test.com", AssetKind.Endpoint, resultAgeMinutes: 20, status: CheckStatus.Error);
        await AddAsset(org.Id, $"ok.{org.Id:N}.test.com", AssetKind.Endpoint, resultAgeMinutes: 20, status: CheckStatus.Ok);
        using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.AcrossTenants<Job>().Where(j => j.TenantId == org.Id).Join(db.AcrossTenants<Asset>(), j => j.AssetId, a => a.Id, (j, a) => a.Name).ToListAsync();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith("err.", n));   // only the one that failed, and every check of it
    }

    [DbFact]
    public async Task The_owner_sets_intervals_on_plans_and_tenants_and_bad_values_are_refused()
    {
        var owner = await EnsureOwner();
        var org = await NewOrg(Tiers.Starter);
        var mia = Factory.As(owner);
        var body = new { maxDomains = "25", maxEndpoints = "100", maxUsers = "10", reportsEnabled = true, retentionMonths = "12" };
        Assert.Equal(HttpStatusCode.NoContent, (await mia.PutAsJsonAsync("/platform/tiers/starter", new { body.maxDomains, body.maxEndpoints, body.maxUsers, body.reportsEnabled, body.retentionMonths, scanIntervalMinutes = "120" })).StatusCode);
        try
        {
            using var scope = Scope();
            Assert.Equal(120, (await scope.ServiceProvider.GetRequiredService<PlanService>().ForAsync(org.Id))!.Limits.ScanIntervalMinutes);
            foreach (var bad in new[] { "0", "-5", "soon", "99999" })
                Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync("/platform/tiers/starter", new { body.maxDomains, body.maxEndpoints, body.maxUsers, body.reportsEnabled, body.retentionMonths, scanIntervalMinutes = bad })).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "starter", scanIntervalMinutes = "15" })).StatusCode);
            Assert.Equal(15, (await scope.ServiceProvider.GetRequiredService<PlanService>().ForAsync(org.Id))!.Limits.ScanIntervalMinutes);
            Assert.Contains("value=\"15\"", await (await mia.GetAsync($"/tenants/{org.Id}")).Content.ReadAsStringAsync());
            await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "starter", scanIntervalMinutes = "default" });
            using var fresh = Scope();
            var plan = (await fresh.ServiceProvider.GetRequiredService<PlanService>().ForAsync(org.Id))!;
            Assert.Null(plan.Limits.ScanIntervalMinutes);                                  // "default" means the server default, not the plan's
            Assert.Equal(-1, plan.Tenant.OverrideScanIntervalMinutes);
            Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "starter", scanIntervalMinutes = "0" })).StatusCode);
        }
        finally { await mia.PutAsJsonAsync("/platform/tiers/starter", new { body.maxDomains, body.maxEndpoints, body.maxUsers, body.reportsEnabled, body.retentionMonths, scanIntervalMinutes = "360" }); }
    }

    // ---- check everything now ----

    [DbFact]
    public async Task Check_everything_now_queues_every_check_whatever_the_interval_and_then_waits()
    {
        var org = await NewOrg(Tiers.Free);                                                 // daily interval, so nothing is due
        var other = await NewOrg(Tiers.Free);
        var domain = await AddAsset(org.Id, $"fresh.{org.Id:N}.test.com", AssetKind.Domain, resultAgeMinutes: 1);
        var endpoint = await AddAsset(org.Id, $"host.{org.Id:N}.test.com:443", AssetKind.Endpoint, resultAgeMinutes: 1);
        await AddAsset(other.Id, $"theirs.{other.Id:N}.test.com", AssetKind.Domain, resultAgeMinutes: 1);
        var expected = ChecksFor(domain) + ChecksFor(endpoint);

        using (var scope = Scope()) await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(default);
        Assert.Empty(await JobsOf(org.Id));                                                 // the scheduler would not touch them

        var ann = Factory.As(org.Admin);
        var first = await ann.PostAsync("/checks/run", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var queued = (await Json(first)).GetProperty("queued").GetInt32();
        Assert.Equal(expected, queued);                                                     // every check that applies to every asset
        Assert.True(expected >= 4);
        var jobs = await JobsOf(org.Id);
        Assert.Equal(expected, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(org.Id, j.TenantId));
        Assert.Empty(await JobsOf(other.Id));                                               // another tenant is untouched

        var again = await ann.PostAsync("/checks/run", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        var wait = (await Json(again)).GetProperty("retryAfterSeconds").GetInt32();
        Assert.InRange(wait, 1, 300);

        await Edit(org.Id, t => t.LastManualRunAt = DateTimeOffset.UtcNow.AddMinutes(-6));  // the cooldown has passed
        var third = await ann.PostAsync("/checks/run", null);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(0, (await Json(third)).GetProperty("queued").GetInt32());              // they are all still waiting
        Assert.Equal(expected, (await JobsOf(org.Id)).Count);
    }

    [DbFact]
    public async Task Only_members_and_admins_can_check_everything_and_the_dashboard_shows_the_button_to_them()
    {
        var org = await NewOrg();
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Viewer).PostAsync("/checks/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).PostAsync("/checks/run", null)).StatusCode);
        var admin = await (await Factory.As(org.Admin).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Check everything now", admin);
        Assert.Contains("once every 5 minutes", admin);
        Assert.DoesNotContain("Check everything now", await (await Factory.As(org.Viewer).GetAsync("/")).Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task The_cooldown_can_be_set_and_an_empty_tenant_just_reports_nothing_to_queue()
    {
        var org = await NewOrg();
        var r = await Factory.As(org.Admin).PostAsync("/checks/run", null);
        Assert.Equal(0, (await Json(r)).GetProperty("queued").GetInt32());
        Assert.Empty(await JobsOf(org.Id));
        using var scope = Scope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants.SingleAsync(t => t.Id == org.Id)).LastManualRunAt);
    }
}

public class SettingsTestTests(PgScanFactory factory) : OrgTestBase<PgScanFactory>(factory), IClassFixture<PgScanFactory>
{
    async Task<List<JsonElement>> Run(HttpClient client, bool sendAlarm = false)
    {
        var response = await client.PostAsJsonAsync("/admin/diagnostics", new { sendAlarm });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    static string StatusOf(List<JsonElement> items, string title) => items.Single(i => i.GetProperty("title").GetString() == title).GetProperty("status").GetString()!;
    static string DetailOf(List<JsonElement> items, string title) => items.Single(i => i.GetProperty("title").GetString() == title).GetProperty("detail").GetString()!;

    [DbFact]
    public async Task Only_admins_can_run_the_settings_test()
    {
        var org = await NewOrg();
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Viewer).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Viewer).GetAsync("/organization")).StatusCode);
        Assert.Contains("data-admin=\"diag\"", await (await Factory.As(org.Admin).GetAsync("/organization")).Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task A_tenant_with_no_alarm_channel_is_told_nobody_will_hear_about_problems()
    {
        var org = await NewOrg();
        var items = await Run(Factory.As(org.Admin));
        Assert.Equal("warning", StatusOf(items, "Alarm settings"));
        Assert.Contains("Nobody will be told", DetailOf(items, "Alarm settings"));
        Assert.Equal("warning", StatusOf(items, "Administrators"));       // a single administrator
        Assert.Contains("only one administrator", DetailOf(items, "Administrators"));
        Assert.DoesNotContain(items, i => i.GetProperty("group").GetString() == "Server"); // server checks are for the platform owner only
    }

    [DbFact]
    public async Task A_second_administrator_with_two_ways_to_sign_in_makes_the_people_checks_pass()
    {
        var org = await NewOrg(admins: 2);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var admin in await db.AcrossTenants<User>().Where(u => u.TenantId == org.Id && u.Role == Role.Admin).ToListAsync())
                for (var i = 0; i < 2; i++) db.Passkeys.Add(new Passkey { TenantId = org.Id, UserId = admin.Id, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1], Name = "k" });
            await db.SaveChangesAsync();
        }
        var items = await Run(Factory.As(org.Admin));
        Assert.Equal("ok", StatusOf(items, "Administrators"));
        Assert.Equal("ok", StatusOf(items, "Passkeys of administrators"));

        await Edit(org.Id, _ => { });
        using var s2 = Scope();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.Passkeys.RemoveRange(await db2.AcrossTenants<Passkey>().Where(p => p.TenantId == org.Id).Take(2).ToListAsync());
        await db2.SaveChangesAsync();
        var after = await Run(Factory.As(org.Admin));
        Assert.Equal("warning", StatusOf(after, "Passkeys of administrators"));
    }

    [DbFact]
    public async Task The_plan_check_warns_when_a_limit_is_reached()
    {
        var org = await NewOrg(Tiers.Free, tweak: t => t.OverrideMaxUsers = 5);   // two users on Free would already be full
        Assert.Equal("ok", StatusOf(await Run(Factory.As(org.Admin)), "Plan and limits"));
        for (var i = 0; i < 3; i++) await AddAsset(org.Id, $"d{i}.{org.Id:N}.test.com");
        var items = await Run(Factory.As(org.Admin));
        Assert.Equal("warning", StatusOf(items, "Plan and limits"));
        Assert.Contains("Domains 3/3", DetailOf(items, "Plan and limits"));
        Assert.Contains("Free plan", DetailOf(items, "Plan and limits"));
    }

    [DbFact]
    public async Task The_checks_check_that_results_are_fresh_for_the_plans_interval()
    {
        var org = await NewOrg(Tiers.Starter);                                     // six hours: fresh means within about 12 hours
        Assert.Equal("info", StatusOf(await Run(Factory.As(org.Admin)), "Results are fresh"));  // no assets yet
        await AddAsset(org.Id, $"fresh.{org.Id:N}.test.com", resultAgeMinutes: 60);
        var recentOnly = await Run(Factory.As(org.Admin));
        Assert.Equal("ok", StatusOf(recentOnly, "Results are fresh"));
        Assert.Contains("6 h", DetailOf(recentOnly, "Check interval"));
        Assert.Contains("from your plan", DetailOf(recentOnly, "Check interval"));

        // One check has not been seen for two days; the others have no result at all yet but are brand new.
        await AddAsset(org.Id, $"old.{org.Id:N}.test.com", resultAgeMinutes: 2 * 24 * 60);
        var items = await Run(Factory.As(org.Admin));
        Assert.Equal("warning", StatusOf(items, "Results are fresh"));
        Assert.Contains($"old.{org.Id:N}.test.com", DetailOf(items, "Results are fresh"));
        Assert.DoesNotContain($"fresh.{org.Id:N}", DetailOf(items, "Results are fresh"));
    }

    [DbFact]
    public async Task A_test_alarm_goes_through_every_channel_and_each_outcome_is_reported()
    {
        var received = new List<string>();
        var listener = new HttpListener();
        var port = Random.Shared.Next(41000, 49000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () => { var c = await listener.GetContextAsync(); received.Add(await new StreamReader(c.Request.InputStream).ReadToEndAsync()); c.Response.StatusCode = 200; c.Response.Close(); });

        var org = await NewOrg();
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
            db.AlertSettings.Add(new AlertSettings { TenantId = org.Id, SlackWebhook = alerts.Protect($"http://127.0.0.1:{port}/slack"), WebhookUrl = alerts.Protect("http://127.0.0.1:1/closed"), EmailTo = "ops@example.test" });
            await db.SaveChangesAsync();
        }
        var items = await Run(Factory.As(org.Admin), sendAlarm: true);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.Single(received);
        Assert.Equal("ok", StatusOf(items, "Slack"));
        Assert.Equal("fail", StatusOf(items, "Webhook"));
        Assert.Equal("fail", StatusOf(items, "Email"));
        Assert.Contains("test alarm was not delivered", DetailOf(items, "Webhook"));
        Assert.Contains("no SMTP host", DetailOf(items, "Email setup"));          // the setup problem, named once
        Assert.Contains("SMTP", DetailOf(items, "Email"));                          // and the failed delivery that follows from it

        // Without ticking the box nothing is sent.
        received.Clear();
        var quiet = await Run(Factory.As(org.Admin), sendAlarm: false);
        Assert.DoesNotContain(quiet, i => i.GetProperty("title").GetString() is "Slack" or "Webhook");
    }

    [DbFact]
    public async Task The_scanner_being_switched_off_is_reported_to_the_admin_and_the_rest_still_runs()
    {
        // This factory has the scanner flag on; the base factory (Scanning:Enabled=false) is the other case.
        var org = await NewOrg();
        Assert.DoesNotContain(await Run(Factory.As(org.Admin)), i => i.GetProperty("title").GetString() == "Scanner");
        await using var off = new PgFactory();
        var other = await NewOrgOn(off);
        var items = (await Json(await off.As(other).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false }))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("fail", StatusOf(items, "Scanner"));
        Assert.Contains("switched off", DetailOf(items, "Scanner"));
        Assert.Contains(items, i => i.GetProperty("title").GetString() == "Plan and limits");
    }

    async Task<Guid> NewOrgOn(PgFactory other)
    {
        using var scope = other.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var t = new Tenant { Name = "Solo", IsManagement = true, TierKey = Tiers.Custom };
        db.Tenants.Add(t);
        var u = new User { TenantId = t.Id, Name = "Solo admin", Role = Role.Admin };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }
}

public class ServerSettingsTestTests(PgProbeFactory factory) : OrgTestBase<PgProbeFactory>(factory), IClassFixture<PgProbeFactory>
{
    [DbFact]
    public async Task The_platform_owner_also_gets_the_server_checks_and_they_say_what_is_wrong()
    {
        var owner = await EnsureOwner();
        var client = Factory.As(owner);
        client.DefaultRequestHeaders.Host = "fq.example.org";                       // plain http on a public name: passkeys will not work
        var response = await client.PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false });
        var json = await Json(response);
        var items = json.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("group").GetString() == "Server").ToList();
        string Status(string t) => items.Single(i => i.GetProperty("title").GetString() == t).GetProperty("status").GetString()!;
        string Detail(string t) => items.Single(i => i.GetProperty("title").GetString() == t).GetProperty("detail").GetString()!;

        Assert.Equal("fail", Status("Secure connection"));
        Assert.Contains("plain http", Detail("Secure connection"));
        Assert.Equal("ok", Status("Database"));
        Assert.Equal("fail", Status("Email server"));
        Assert.Contains("smtp.invalid.test:587", Detail("Email server"));
        Assert.Contains("connection refused", Detail("Email server"));
        Assert.Equal("ok", Status("Single sign-on"));
        Assert.Equal("warning", Status("Open sign-up"));
        Assert.Equal("ok", Status("Name resolution"));
        Assert.Equal("warning", Status("Certificate Transparency"));
        Assert.Contains("timeout", Detail("Certificate Transparency"));
        Assert.Equal("info", Status("Private networks"));
        Assert.Equal("info", Status("Free public check"));
        Assert.Contains("Passkey address", items.Select(i => i.GetProperty("title").GetString()));   // Auth:Origin differs from the address in use
        Assert.Matches(@"\d+ OK, \d+ warnings, \d+ failed", json.GetProperty("summary").GetString()!);
    }

    [DbFact]
    public async Task A_tenant_admin_never_sees_server_details()
    {
        var org = await NewOrg();
        var text = await (await Factory.As(org.Admin).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("smtp.invalid.test", text);
        Assert.DoesNotContain("idp.invalid.test", text);
        Assert.DoesNotContain("fq.example.org", text);
    }
}
