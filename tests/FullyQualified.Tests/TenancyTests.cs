using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FullyQualified.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FullyQualified.Tests;

/// <summary>Runs only when FQ_TEST_DB points at a Postgres server (without a database name), e.g. "Host=localhost;Username=postgres;Password=postgres".</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public static readonly string? Server = Environment.GetEnvironmentVariable("FQ_TEST_DB");
    public DbFactAttribute() { if (Server is null) Skip = "Set FQ_TEST_DB to run the database tests"; }
}

/// <summary>The real app on a fresh Postgres database. Requests act as the user named in the X-User header, so no passkey ceremony is needed.</summary>
public class PgFactory : WebApplicationFactory<Program>
{
    public string Database { get; } = "fq_test_" + Guid.NewGuid().ToString("N");
    string Connection => $"{DbFactAttribute.Server};Database={Database}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", Connection);
        builder.UseSetting("Scanning:Enabled", "false");
        builder.ConfigureServices(s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderUserHandler>("Test", _ => { });
            s.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = "Test");
        });
    }

    public HttpClient As(Guid? user)
    {
        var client = CreateClient(new() { AllowAutoRedirect = false });
        if (user is { } id) client.DefaultRequestHeaders.Add("X-User", id.ToString());
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || DbFactAttribute.Server is null) return;
        NpgsqlConnection.ClearAllPools();
        using var admin = new NpgsqlConnection($"{DbFactAttribute.Server};Database=postgres");
        admin.Open();
        using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE)", admin);
        drop.ExecuteNonQuery();
    }

    class HeaderUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var raw) || !Guid.TryParse(raw, out var id)) return AuthenticateResult.NoResult();
            var db = Context.RequestServices.GetRequiredService<AppDbContext>();
            var u = await (from user in db.AcrossTenants<User>() join t in db.Tenants on user.TenantId equals t.Id where user.Id == id
                           select new SessionUser(user.Id, user.Name, user.Role, user.TenantId, t.IsManagement)).SingleAsync();
            return AuthenticateResult.Success(new AuthenticationTicket(Session.Principal(u), "Test"));
        }
    }
}

/// <summary>Two customer tenants and the management tenant, each with people and monitoring data.</summary>
public record World(Guid Mgmt, Guid Acme, Guid Globex, Guid MgmtAdmin, Guid AcmeAdmin, Guid AcmeViewer, Guid GlobexAdmin, Guid GlobexViewer);

public class TenancyTests : IClassFixture<PgFactory>
{
    readonly PgFactory factory;
    readonly Task<World> world;
    public TenancyTests(PgFactory factory)
    {
        this.factory = factory;
        world = DbFactAttribute.Server is null ? Task.FromResult<World>(null!) : Seed(factory.Services);
    }

    /// <summary>A scope for the given tenant, or for background work when null.</summary>
    static IServiceScope Scope(IServiceProvider sp, Guid? tenant)
    {
        var scope = sp.CreateScope();
        var t = scope.ServiceProvider.GetRequiredService<TenantScope>();
        if (tenant is { } id) t.Id = id; else t.All = true;
        return scope;
    }

    static readonly SemaphoreSlim SeedOnce = new(1, 1);
    static World? seeded;

    async Task<World> Seed(IServiceProvider sp)
    {
        // Touching the database first applies the migrations through the app's own start-up.
        using var scope = Scope(sp, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        if (await db.Tenants.AnyAsync()) return await Load(db);

        var mgmt = new Tenant { Name = "Management", IsManagement = true };
        var acme = new Tenant { Name = "Acme" };
        var globex = new Tenant { Name = "Globex" };
        db.Tenants.AddRange(mgmt, acme, globex);
        User U(Tenant t, string name, Role r, string? email = null) => new() { TenantId = t.Id, Name = name, Role = r, Email = email, LastLoginAt = DateTimeOffset.UtcNow.AddHours(-2) };
        var users = new[] { U(mgmt, "Mia", Role.Admin, "mia@mgmt.test"), U(acme, "Ann", Role.Admin, "ann@acme.test"), U(acme, "Vic", Role.Viewer, "vic@acme.test"),
                            U(globex, "Gus", Role.Admin, "gus@globex.test"), U(globex, "Gia", Role.Viewer, "gia@globex.test") };
        db.Users.AddRange(users);

        Asset A(Tenant t, string name, AssetKind k) => new() { TenantId = t.Id, Name = name, Kind = k };
        var a1 = A(acme, "acme.example", AssetKind.Domain); var a2 = A(acme, "shop.acme.example:443", AssetKind.Endpoint);
        var g1 = A(globex, "globex.example", AssetKind.Domain);
        db.Assets.AddRange(a1, a2, g1);
        CheckResult R(Asset a, string type, CheckStatus s, int hoursAgo) => new() { TenantId = a.TenantId, AssetId = a.Id, CheckType = type, Status = s, Summary = "Sum_DnsOk", ObservedAt = DateTimeOffset.UtcNow.AddHours(-hoursAgo) };
        db.Results.AddRange(R(a1, "dns", CheckStatus.Ok, 30), R(a1, "dns", CheckStatus.Warning, 2), R(a2, "tls", CheckStatus.Ok, 3), R(a2, "tls", CheckStatus.Critical, 1), R(g1, "dns", CheckStatus.Error, 1));
        db.Certificates.Add(new CertificateRecord { TenantId = acme.Id, AssetId = a2.Id, Source = "endpoint", Host = "shop.acme.example", Subject = "CN=shop", Issuer = "R11", Fingerprint = "f1",
            NotBefore = DateTimeOffset.UtcNow.AddDays(-80), NotAfter = DateTimeOffset.UtcNow.AddDays(10) });
        db.Changes.Add(new Change { TenantId = acme.Id, AssetId = a1.Id, CheckType = "dns", Key = "spf", Before = "a", After = "b" });
        await db.SaveChangesAsync();
        return await Load(db);
    }

    static async Task<World> Load(AppDbContext db)
    {
        var t = await db.Tenants.ToDictionaryAsync(x => x.Name, x => x.Id);
        var u = await db.AcrossTenants<User>().ToDictionaryAsync(x => x.Name, x => x.Id);
        return new World(t["Management"], t["Acme"], t["Globex"], u["Mia"], u["Ann"], u["Vic"], u["Gus"], u["Gia"]);
    }

    async Task<World> W() { await SeedOnce.WaitAsync(); try { return seeded ??= await world; } finally { SeedOnce.Release(); } }

    // ---- isolation at the data layer ----

    [DbFact]
    public async Task A_tenant_only_sees_its_own_rows()
    {
        var w = await W();
        using var scope = Scope(factory.Services, w.Acme);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(["acme.example", "shop.acme.example:443"], await db.Assets.OrderBy(a => a.Name).Select(a => a.Name).ToListAsync());
        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.TenantId != w.Acme);
        Assert.Equal(4, await db.Results.CountAsync());
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Single(await db.Certificates.ToListAsync());
        Assert.DoesNotContain(await db.Users.ToListAsync(), u => u.TenantId != w.Acme);
    }

    [DbFact]
    public async Task A_request_without_a_tenant_sees_nothing()
    {
        await W();
        using var scope = factory.Services.CreateScope(); // neither a tenant nor background work: fails closed
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.Results.ToListAsync());
    }

    [DbFact]
    public async Task Background_work_sees_every_tenant()
    {
        var w = await W();
        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.Assets.Select(a => a.Name).ToListAsync();
        Assert.All(new[] { "acme.example", "shop.acme.example:443", "globex.example" }, n => Assert.Contains(n, names));
        Assert.Equal(new[] { w.Acme, w.Globex }.Order(), (await db.Assets.Select(a => a.TenantId).Distinct().ToListAsync()).Order());
    }

    [DbFact]
    public async Task New_rows_get_the_current_tenant_and_a_row_for_another_tenant_is_refused()
    {
        var w = await W();
        using var scope = Scope(factory.Services, w.Globex);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mine = new Asset { Name = "stamped.globex.example", Kind = AssetKind.Domain };
        db.Assets.Add(mine);
        await db.SaveChangesAsync();
        Assert.Equal(w.Globex, mine.TenantId);

        db.Assets.Add(new Asset { TenantId = w.Acme, Name = "smuggled.example", Kind = AssetKind.Domain });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task A_row_without_any_tenant_is_refused_rather_than_saved_somewhere()
    {
        await W();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Assets.Add(new Asset { Name = "orphan.example", Kind = AssetKind.Domain });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task There_is_exactly_one_management_tenant()
    {
        await W();
        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(new Tenant { Name = "Second boss", IsManagement = true });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DbFact]
    public async Task Scheduling_stamps_each_job_with_its_assets_tenant()
    {
        var w = await W();
        using var scope = Scope(factory.Services, null);
        await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.Jobs.ToListAsync();
        var owner = await db.Assets.ToDictionaryAsync(a => a.Id, a => a.TenantId);
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j => Assert.Equal(owner[j.AssetId], j.TenantId));
        Assert.Contains(jobs, j => j.TenantId == w.Acme);
        Assert.Contains(jobs, j => j.TenantId == w.Globex);
    }

    // ---- platform screens ----

    [DbFact]
    public async Task Tenant_stats_count_assets_users_and_problems_per_tenant()
    {
        var w = await W();
        using var scope = Scope(factory.Services, w.Mgmt);
        var stats = (await scope.ServiceProvider.GetRequiredService<TenantStats>().ForAsync()).ToDictionary(s => s.Tenant.Id);
        Assert.True(stats[w.Mgmt].Tenant.IsManagement);
        var acme = stats[w.Acme];
        Assert.Equal((2, 1, 1, 2), (acme.Assets, acme.Domains, acme.Endpoints, acme.Users));
        Assert.Equal((1, 1), (acme.Certificates, acme.Expiring));
        Assert.Equal(2, acme.Problems); // latest results: dns Warning, tls Critical
        Assert.Equal(1, acme.Changes7d);
        Assert.NotNull(acme.LastLogin);
        Assert.NotNull(acme.LastScan);
        Assert.Equal(1, stats[w.Globex].Problems);
        Assert.Equal(0, stats[w.Mgmt].Assets);
    }

    [DbFact]
    public async Task The_log_counts_results_per_day_and_status_for_one_tenant()
    {
        var w = await W();
        using var scope = Scope(factory.Services, w.Acme);
        var log = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter(30));
        Assert.Equal(4, log.Total);
        Assert.Equal(2, log.Ok);
        Assert.Equal(4, log.ResultsPerDay.Values.Sum(v => v.Sum()));
        Assert.Equal(1, log.ByCheck["tls"][(int)CheckStatus.Critical]);
        Assert.Equal(4, log.RowCount);
        Assert.Equal("shop.acme.example:443", log.Rows[0].Asset); // newest first

        var tlsOnly = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter(30, Check: "tls"));
        Assert.Equal(2, tlsOnly.Total);
        var critical = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter(30, CheckStatus.Critical));
        Assert.Single(critical.Rows);
    }

    // ---- who may do what over HTTP ----

    [DbFact]
    public async Task Tenant_admins_manage_their_own_users_and_cannot_touch_other_tenants()
    {
        var w = await W();
        var ann = factory.As(w.AcmeAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PutAsJsonAsync($"/admin/users/{w.GlobexViewer}/role", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.DeleteAsync($"/admin/users/{w.GlobexViewer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsync($"/admin/users/{w.GlobexViewer}/reset-passkey", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PutAsJsonAsync($"/admin/users/{w.AcmeViewer}/role", new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ann.PutAsJsonAsync($"/admin/users/{w.AcmeAdmin}/role", new { role = "Viewer" })).StatusCode); // last admin

        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(Role.Viewer, (await db.Users.SingleAsync(u => u.Id == w.GlobexViewer)).Role);
        await ann.PutAsJsonAsync($"/admin/users/{w.AcmeViewer}/role", new { role = "Viewer" });
    }

    [DbFact]
    public async Task Only_management_admins_can_use_the_platform_endpoints_and_screens()
    {
        var w = await W();
        foreach (var user in new[] { w.AcmeAdmin, w.AcmeViewer })
        {
            var c = factory.As(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/platform/tenants", new { name = "Sneaky" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/tenants")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/tenants/{w.Globex}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(w.AcmeAdmin).PostAsJsonAsync("/admin/invites", new { role = "Admin", tenantId = w.Globex })).StatusCode);

        var mia = factory.As(w.MgmtAdmin);
        var page = await (await mia.GetAsync("/tenants")).Content.ReadAsStringAsync();
        Assert.All(new[] { "Management", "Acme", "Globex" }, name => Assert.Contains(name, page));
        var detail = await (await mia.GetAsync($"/tenants/{w.Acme}")).Content.ReadAsStringAsync();
        Assert.Contains("Ann", detail);
        Assert.Contains("Vic", detail);
        Assert.DoesNotContain("Gus", detail);
    }

    [DbFact]
    public async Task The_management_admin_creates_tenants_and_invites_into_any_tenant()
    {
        var w = await W();
        var mia = factory.As(w.MgmtAdmin);
        var created = await mia.PostAsJsonAsync("/platform/tenants", new { name = "Initech", adminEmail = "boss@initech.test" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("/invite/", await created.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await mia.PostAsJsonAsync("/platform/tenants", new { name = "Initech" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mia.PostAsJsonAsync("/platform/tenants", new { name = "Other", adminEmail = "ann@acme.test" })).StatusCode);

        // Globex is on Free with two users, so it is full; the owner is held to the limit like anyone, until the plan is raised.
        Assert.Equal(HttpStatusCode.Conflict, (await mia.PostAsJsonAsync("/admin/invites", new { role = "Viewer", tenantId = w.Globex })).StatusCode);
        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Tenants.SingleAsync(t => t.Id == w.Globex)).TierKey = Tiers.Starter;
        await db.SaveChangesAsync();
        var invite = await mia.PostAsJsonAsync("/admin/invites", new { role = "Viewer", tenantId = w.Globex });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.Contains(await db.Invites.ToListAsync(), i => i.TenantId == w.Globex && i.Role == Role.Viewer && i.RecoveryForUser is null);
        var initech = await db.Tenants.SingleAsync(t => t.Name == "Initech");
        Assert.False(initech.IsManagement);
        Assert.Contains(await db.Invites.ToListAsync(), i => i.TenantId == initech.Id && i.Role == Role.Admin && i.Email == "boss@initech.test");
    }

    // ---- passkey recovery ----

    [DbFact]
    public async Task An_admin_can_issue_a_recovery_link_for_a_user_in_their_tenant()
    {
        var w = await W();
        var response = await factory.As(w.AcmeAdmin).PostAsync($"/admin/users/{w.AcmeViewer}/reset-passkey", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("url").GetString()!;
        Assert.Contains("/recover/", url);

        var page = await (await factory.As(null).GetAsync(new Uri(url).AbsolutePath)).Content.ReadAsStringAsync();
        Assert.Contains("data-passkey=\"register\"", page);   // a valid link offers to set up a passkey
        Assert.DoesNotContain("name=\"pk-name\"", page);      // and does not ask for a name: the account already exists

        var again = await factory.As(w.AcmeAdmin).PostAsync($"/admin/users/{w.AcmeViewer}/reset-passkey", null);
        var newer = (await again.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("url").GetString()!;
        Assert.NotEqual(url, newer);
        var old = await (await factory.As(null).GetAsync(new Uri(url).AbsolutePath)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-passkey=\"register\"", old); // issuing a new link retires the earlier one
    }

    [DbFact]
    public async Task The_operator_command_prints_a_working_recovery_link()
    {
        var w = await W();
        var output = new StringWriter();
        Assert.Equal(0, await OperatorCommands.RecoverAsync(factory.Services, "gus@globex.test", "https://fq.example/", output));
        var line = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("https://fq.example/recover/"));
        var token = line[(line.LastIndexOf('/') + 1)..];
        using var scope = Scope(factory.Services, null);
        var passkeys = scope.ServiceProvider.GetRequiredService<PasskeyService>();
        var invite = await passkeys.FindInvite(token, recovery: true);
        Assert.Equal(w.GlobexAdmin, invite!.RecoveryForUser);
        Assert.Null(await passkeys.FindInvite(token, recovery: false)); // a recovery link is not an invitation

        var missing = new StringWriter();
        Assert.Equal(1, await OperatorCommands.RecoverAsync(factory.Services, "nobody@nowhere.test", "https://fq.example", missing));
    }

    // ---- alarms ----

    /// <summary>Read through a fresh scope, so a context that tracked an older copy of the row cannot hide a change.</summary>
    async Task<AlertConfig> Config(Guid tenant)
    {
        using var fresh = Scope(factory.Services, null);
        return await fresh.ServiceProvider.GetRequiredService<AlertService>().ConfigFor(tenant);
    }

    [DbFact]
    public async Task Alarm_settings_are_per_tenant_and_secrets_are_not_stored_or_shown_in_the_clear()
    {
        var w = await W();
        var secret = "https://hooks.example.test/services/T000/B000/SECRETTOKEN";
        var ann = factory.As(w.AcmeAdmin);
        var saved = await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, slackWebhook = secret, emailTo = "ops@acme.test, SEC@acme.test", minStatus = "Error", notifyRecovery = false, notifyDrift = true });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AlertSettings.SingleAsync(s => s.TenantId == w.Acme);
        Assert.DoesNotContain("SECRETTOKEN", row.SlackWebhook);
        Assert.Equal("ops@acme.test, sec@acme.test", row.EmailTo);
        var config = await Config(w.Acme);
        Assert.Equal((secret, CheckStatus.Error, false), (config.SlackWebhook, config.MinStatus, config.NotifyRecovery));
        Assert.Null((await Config(w.Globex)).SlackWebhook);

        var page = await (await ann.GetAsync("/alerts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRETTOKEN", page);
        Assert.Contains("hooks.example.test", page);
        Assert.DoesNotContain("hooks.example.test", await (await factory.As(w.GlobexAdmin).GetAsync("/alerts")).Content.ReadAsStringAsync());

        // Leaving the field empty keeps the secret; the remove box clears it.
        await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, slackWebhook = "", minStatus = "Warning", notifyRecovery = true, notifyDrift = true });
        Assert.Equal(secret, (await Config(w.Acme)).SlackWebhook);
        await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, clearSlack = true, minStatus = "Warning", notifyRecovery = true, notifyDrift = true });
        Assert.Null((await Config(w.Acme)).SlackWebhook);
    }

    [DbFact]
    public async Task Only_admins_change_alarm_settings_and_bad_input_is_refused()
    {
        var w = await W();
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(w.AcmeViewer).PutAsJsonAsync("/admin/alerts/", new { enabled = true, minStatus = "Warning" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(w.AcmeViewer).GetAsync("/alerts")).StatusCode);
        var ann = factory.As(w.AcmeAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, minStatus = "Ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, minStatus = "Warning", emailTo = "not-an-address" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, minStatus = "Warning", webhookUrl = "ftp://example.test/x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/admin/alerts/", new { enabled = true, minStatus = "Warning", emailTo = string.Join(",", Enumerable.Range(0, 11).Select(i => $"u{i}@x.test")) })).StatusCode);
    }

    [DbFact]
    public async Task Alarms_are_delivered_to_the_tenants_channel_and_every_attempt_is_logged()
    {
        var w = await W();
        var received = new List<string>();
        var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            received.Add(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync());
            ctx.Response.StatusCode = 200; ctx.Response.Close();
        });

        using var scope = Scope(factory.Services, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        db.AlertSettings.RemoveRange(await db.AlertSettings.ToListAsync());
        db.AlertSettings.Add(new AlertSettings { TenantId = w.Acme, SlackWebhook = alerts.Protect($"http://127.0.0.1:{port}/hook"), WebhookUrl = alerts.Protect("http://127.0.0.1:1/closed") });
        await db.SaveChangesAsync();

        await alerts.SendAsync(w.Acme, "FullyQualified: acme.example Warning", "details", default);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.Single(received);
        Assert.Contains("acme.example Warning", received[0]);
        var log = await db.AlertLog.Where(a => a.TenantId == w.Acme).OrderBy(a => a.Channel).ToListAsync();
        Assert.Equal([("slack", true), ("webhook", false)], log.Select(l => (l.Channel, l.Success)));
        Assert.NotNull(log[1].Error);
        Assert.Empty(await db.AlertLog.Where(a => a.TenantId == w.Globex).ToListAsync());
        db.AlertSettings.RemoveRange(await db.AlertSettings.Where(s => s.TenantId == w.Acme).ToListAsync());
        await db.SaveChangesAsync();
    }

    // ---- the screens render ----

    [DbFact]
    public async Task The_log_and_alarm_screens_render_for_a_tenant_admin()
    {
        var w = await W();
        var ann = factory.As(w.AcmeAdmin);
        var log = await ann.GetAsync("/log?days=30&check=tls");
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        var html = await log.Content.ReadAsStringAsync();
        Assert.Contains("<svg class=\"chart\"", html);
        Assert.Contains("shop.acme.example:443", html);
        Assert.DoesNotContain("globex.example", html);
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync("/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync("/users")).StatusCode);
        var users = await (await ann.GetAsync("/users")).Content.ReadAsStringAsync();
        Assert.Contains("Vic", users);
        Assert.DoesNotContain("Gus", users);
        Assert.Contains(DateTimeOffset.UtcNow.AddHours(-2).ToString("yyyy-MM-dd"), users); // last login is shown
    }
}
