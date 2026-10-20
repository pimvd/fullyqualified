using System.Net;
using System.Net.Http.Json;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

/// <summary>Plans, limits, retention and deleting tenants, against a real database. Every test makes its own tenants, so they do not depend on each other.</summary>
public class PlanDbTests : IClassFixture<PgFactory>
{
    readonly PgFactory factory;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Guid mgmtAdmin;
    public PlanDbTests(PgFactory factory) => this.factory = factory;

    IServiceScope Scope() { var s = factory.Services.CreateScope(); s.ServiceProvider.GetRequiredService<TenantScope>().All = true; return s; }

    /// <summary>The management tenant and its owner exist once per database.</summary>
    async Task<Guid> Owner()
    {
        await Gate.WaitAsync();
        try
        {
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            if (mgmtAdmin != Guid.Empty && await db.AcrossTenants<User>().AnyAsync(u => u.Id == mgmtAdmin)) return mgmtAdmin;
            var tenant = new Tenant { Name = "Boss " + Guid.NewGuid().ToString("N")[..6], IsManagement = true, TierKey = Tiers.Custom };
            db.Tenants.Add(tenant);
            var owner = new User { TenantId = tenant.Id, Name = "Owner", Role = Role.Admin };
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            return mgmtAdmin = owner.Id;
        }
        finally { Gate.Release(); }
    }

    record Org(Guid Id, string Name, Guid Admin, Guid Viewer);

    async Task<Org> NewOrg(string tier = Tiers.Free, int domains = 0, int endpoints = 0)
    {
        await Owner();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = "Org " + Guid.NewGuid().ToString("N")[..8];
        var t = new Tenant { Name = name, TierKey = tier };
        db.Tenants.Add(t);
        var admin = new User { TenantId = t.Id, Name = "Admin of " + name, Role = Role.Admin, Email = $"admin@{t.Id:N}.test" };
        var viewer = new User { TenantId = t.Id, Name = "Viewer of " + name, Role = Role.Viewer };
        db.Users.AddRange(admin, viewer);
        for (var i = 0; i < domains; i++) db.Assets.Add(new Asset { TenantId = t.Id, Name = $"d{i}.{t.Id:N}.test.com", Kind = AssetKind.Domain });
        for (var i = 0; i < endpoints; i++) db.Assets.Add(new Asset { TenantId = t.Id, Name = $"e{i}.{t.Id:N}.test.com:443", Kind = AssetKind.Endpoint });
        await db.SaveChangesAsync();
        return new Org(t.Id, name, admin.Id, viewer.Id);
    }

    async Task<Plan> PlanOf(Guid tenant)
    {
        using var scope = Scope();
        return (await scope.ServiceProvider.GetRequiredService<PlanService>().ForAsync(tenant))!;
    }

    async Task Edit(Guid tenant, Action<Tenant> change)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = await db.Tenants.SingleAsync(x => x.Id == tenant);
        change(t);
        await db.SaveChangesAsync();
    }

    // ---- plans ----

    [DbFact]
    public async Task A_new_tenant_is_on_free_and_the_management_tenant_is_unlimited()
    {
        var org = await NewOrg();
        var plan = await PlanOf(org.Id);
        Assert.Equal(("free", new PlanLimits(3, 10, 2, false, 1, 1440)), (plan.Tier.Key, plan.Limits));

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mgmt = await db.Tenants.SingleAsync(t => t.IsManagement);
        var boss = (await scope.ServiceProvider.GetRequiredService<PlanService>().ForAsync(mgmt.Id))!;
        Assert.Equal(new PlanLimits(null, null, null, true, null, null), boss.Limits);
        Assert.Equal(4, await db.Tiers.CountAsync());
    }

    [DbFact]
    public async Task The_owner_edits_a_tier_and_every_tenant_on_it_follows()
    {
        var owner = await Owner();
        var org = await NewOrg(Tiers.Starter);
        var client = factory.As(owner);
        var saved = await client.PutAsJsonAsync("/platform/tiers/starter", new { maxDomains = "40", maxEndpoints = "", maxUsers = "12", reportsEnabled = true, retentionMonths = "18", scanIntervalMinutes = "360" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        try
        {
            Assert.Equal(new PlanLimits(40, null, 12, true, 18, 360), (await PlanOf(org.Id)).Limits);
            // The page shows what was saved.
            Assert.Contains("value=\"40\"", await (await client.GetAsync("/limits")).Content.ReadAsStringAsync());
        }
        finally
        {
            await client.PutAsJsonAsync("/platform/tiers/starter", new { maxDomains = "25", maxEndpoints = "100", maxUsers = "10", reportsEnabled = true, retentionMonths = "12", scanIntervalMinutes = "360" });
        }
        Assert.Equal(new PlanLimits(25, 100, 10, true, 12, 360), (await PlanOf(org.Id)).Limits);
    }

    [DbFact]
    public async Task Only_the_owner_edits_tiers_and_bad_values_are_refused()
    {
        var owner = await Owner();
        var org = await NewOrg();
        var body = new { maxDomains = "5", maxEndpoints = "5", maxUsers = "5", reportsEnabled = false, retentionMonths = "1" };
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(org.Admin).PutAsJsonAsync("/platform/tiers/free", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(org.Admin).GetAsync("/limits")).StatusCode);
        var mia = factory.As(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync("/platform/tiers/free", body with { maxDomains = "many" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync("/platform/tiers/free", body with { maxUsers = "-4" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync("/platform/tiers/free", body with { retentionMonths = "0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync("/platform/tiers/free", body with { retentionMonths = "999" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mia.PutAsJsonAsync("/platform/tiers/nope", body)).StatusCode);
        Assert.Equal(3, (await PlanOf(org.Id)).Limits.MaxDomains); // nothing changed
    }

    [DbFact]
    public async Task The_owner_can_override_a_tenant_and_switch_it_to_custom()
    {
        var owner = await Owner();
        var org = await NewOrg();
        var mia = factory.As(owner);
        var put = await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "custom", maxDomains = "7", maxEndpoints = "unlimited", maxUsers = "", reports = "no", retentionMonths = "6" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var plan = await PlanOf(org.Id);
        Assert.Equal(("custom", new PlanLimits(7, null, null, false, 6, null)), (plan.Tier.Key, plan.Limits));
        Assert.True(plan.Overridden);

        var page = await (await mia.GetAsync($"/tenants/{org.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"7\"", page);
        Assert.Contains("value=\"unlimited\"", page);

        await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "free", maxDomains = "", maxEndpoints = "", maxUsers = "", reports = "", retentionMonths = "" });
        var back = await PlanOf(org.Id);
        Assert.Equal(("free", new PlanLimits(3, 10, 2, false, 1, 1440)), (back.Tier.Key, back.Limits));
        Assert.False(back.Overridden);

        foreach (var bad in new object[] { new { tier = "gold" }, new { tier = "free", maxDomains = "x" }, new { tier = "free", reports = "maybe" }, new { tier = "free", retentionMonths = "0" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await mia.PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(org.Admin).PutAsJsonAsync($"/platform/tenants/{org.Id}/plan", new { tier = "custom" })).StatusCode);
    }

    [DbFact]
    public async Task A_tenant_admin_sees_the_plan_and_usage_but_cannot_change_it()
    {
        var org = await NewOrg(Tiers.Free, domains: 2, endpoints: 1);
        await Edit(org.Id, t => t.OverrideMaxDomains = 9);
        var page = await (await factory.As(org.Admin).GetAsync("/organization")).Content.ReadAsStringAsync();
        Assert.Contains("Plan: Free", page);
        Assert.Contains("agreed for this organization", page);
        Assert.Matches(@"Domains</td><td>2</td><td>9", page);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(org.Viewer).GetAsync("/organization")).StatusCode);
    }

    // ---- enforcement ----

    [DbFact]
    public async Task Assets_stop_at_the_limit_per_kind()
    {
        var org = await NewOrg(Tiers.Free, domains: 3, endpoints: 4);
        var plan = await PlanOf(org.Id);
        Assert.Equal("Limit_Domains", plan.Blocks(AssetKind.Domain));
        Assert.Null(plan.Blocks(AssetKind.Endpoint));
        await Edit(org.Id, t => t.OverrideMaxEndpoints = 4);
        Assert.Equal("Limit_Endpoints", (await PlanOf(org.Id)).Blocks(AssetKind.Endpoint));
        await Edit(org.Id, t => t.OverrideMaxDomains = -1);
        Assert.Null((await PlanOf(org.Id)).Blocks(AssetKind.Domain));
    }

    [DbFact]
    public async Task Discovery_adds_hosts_only_while_the_endpoint_limit_has_room()
    {
        var org = await NewOrg(Tiers.Free, domains: 1, endpoints: 2);
        await Edit(org.Id, t => t.OverrideMaxEndpoints = 5);                       // room for three more
        using var scope = Scope();
        var runner = scope.ServiceProvider.GetRequiredService<CheckRunner>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var origin = await db.Assets.FirstAsync(a => a.TenantId == org.Id && a.Kind == AssetKind.Domain);
        var found = Enumerable.Range(0, 8).Select(i => $"found{i}.{org.Id:N}.test.com").ToList();
        await runner.ExecuteAsync(origin, new FakeCheck(new CheckOutcome(CheckStatus.Ok, "Sum_CtOk|8|8", [], null, found)), default);

        var plan = await PlanOf(org.Id);
        Assert.Equal(5, plan.Usage.Endpoints);
        Assert.Equal(3, await db.Assets.CountAsync(a => a.TenantId == org.Id && a.Discovered));
        Assert.All(await db.Assets.Where(a => a.Discovered).ToListAsync(), a => Assert.NotEqual(Guid.Empty, a.TenantId));
    }

    sealed class FakeCheck(CheckOutcome outcome) : ICheck
    {
        public string Type => "ct";
        public bool AppliesTo(Asset asset) => true;
        public Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct) => Task.FromResult(outcome);
    }

    [DbFact]
    public async Task Open_invitations_count_against_the_user_limit()
    {
        var org = await NewOrg(Tiers.Free);                                         // two users, limit two
        var ann = factory.As(org.Admin);
        var full = await ann.PostAsJsonAsync("/admin/invites", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("user_limit", await full.Content.ReadAsStringAsync());

        await Edit(org.Id, t => t.OverrideMaxUsers = 3);
        Assert.Equal(HttpStatusCode.OK, (await ann.PostAsJsonAsync("/admin/invites", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ann.PostAsJsonAsync("/admin/invites", new { role = "Viewer" })).StatusCode); // the first one holds the last seat
        Assert.Equal(1, (await PlanOf(org.Id)).Usage.PendingInvites);

        // The owner is held to the same limit when inviting into the tenant.
        Assert.Equal(HttpStatusCode.Conflict, (await factory.As(await Owner()).PostAsJsonAsync("/admin/invites", new { role = "Viewer", tenantId = org.Id })).StatusCode);
    }

    [DbFact]
    public async Task The_nis2_report_is_only_for_plans_that_include_it()
    {
        var free = await NewOrg(Tiers.Free);
        var starter = await NewOrg(Tiers.Starter);
        var onFree = await factory.As(free.Admin).GetAsync("/reports/nis2.pdf");
        Assert.Equal(HttpStatusCode.Redirect, onFree.StatusCode);
        Assert.Equal("/reports", onFree.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await factory.As(free.Admin).GetAsync("/reports/nis2.xlsx")).StatusCode);
        var notice = await (await factory.As(free.Admin).GetAsync("/reports")).Content.ReadAsStringAsync();
        Assert.Contains("not part of this plan", notice);
        Assert.DoesNotContain("nis2.pdf", notice);

        var ok = await factory.As(starter.Admin).GetAsync("/reports/nis2.pdf");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/pdf", ok.Content.Headers.ContentType?.MediaType);

        await Edit(free.Id, t => t.OverrideReports = true);                          // an override can grant it
        Assert.Equal(HttpStatusCode.OK, (await factory.As(free.Admin).GetAsync("/reports/nis2.pdf")).StatusCode);
    }

    // ---- retention ----

    [DbFact]
    public async Task Retention_deletes_old_history_but_keeps_the_newest_result_and_the_newest_good_one()
    {
        var org = await NewOrg(Tiers.Free, domains: 1);                              // free keeps one month
        var forever = await NewOrg(Tiers.Custom, domains: 1);                        // custom keeps everything
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task<Guid> Seed(Org o)
        {
            var asset = await db.Assets.FirstAsync(a => a.TenantId == o.Id);
            CheckResult R(string type, CheckStatus s, int daysAgo) => new() { TenantId = o.Id, AssetId = asset.Id, CheckType = type, Status = s, Summary = "Sum_DnsOk", ObservedAt = now.AddDays(-daysAgo) };
            db.Results.AddRange(
                R("dns", CheckStatus.Ok, 90), R("dns", CheckStatus.Warning, 60), R("dns", CheckStatus.Ok, 45),   // all old, and a newer good result exists
                R("dns", CheckStatus.Error, 40), R("dns", CheckStatus.Error, 20), R("dns", CheckStatus.Ok, 5),    // the error at 40 is old and goes; 20 and 5 are inside the month
                R("tls", CheckStatus.Error, 100), R("tls", CheckStatus.Error, 50),                                // only errors, and old: the newest of them must stay
                R("ct", CheckStatus.Ok, 60), R("ct", CheckStatus.Error, 50), R("ct", CheckStatus.Error, 10));     // the good one at 60 is the baseline for later errors: it must stay
            db.Changes.AddRange(new Change { TenantId = o.Id, AssetId = asset.Id, CheckType = "dns", Key = "spf", DetectedAt = now.AddDays(-50) },
                                new Change { TenantId = o.Id, AssetId = asset.Id, CheckType = "dns", Key = "spf", DetectedAt = now.AddDays(-3) });
            db.AlertLog.AddRange(new AlertLogEntry { TenantId = o.Id, Channel = "email", Subject = "old", At = now.AddDays(-70), Success = true },
                                 new AlertLogEntry { TenantId = o.Id, Channel = "email", Subject = "new", At = now.AddDays(-2), Success = true });
            await db.SaveChangesAsync();
            return asset.Id;
        }
        await Seed(org); await Seed(forever);

        var removed = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(now);
        Assert.True(removed.Results >= 3 && removed.Changes >= 1 && removed.AlertLog >= 1);

        var kept = (await db.Results.Where(r => r.TenantId == org.Id).Select(r => new { r.CheckType, r.ObservedAt }).ToListAsync())
            .Select(r => (r.CheckType, Days: (int)(now - r.ObservedAt).TotalDays)).ToHashSet();
        Assert.Equal(new HashSet<(string, int)> { ("tls", 50), ("dns", 20), ("dns", 5), ("ct", 60), ("ct", 10) }, kept);
        Assert.Equal(["spf"], await db.Changes.Where(c => c.TenantId == org.Id).Select(c => c.Key).ToListAsync());
        Assert.Equal(["new"], await db.AlertLog.Where(a => a.TenantId == org.Id).Select(a => a.Subject).ToListAsync());

        Assert.Equal(11, await db.Results.CountAsync(r => r.TenantId == forever.Id));                 // untouched
        Assert.Equal(2, await db.Changes.CountAsync(c => c.TenantId == forever.Id));
    }

    [DbFact]
    public async Task A_longer_override_keeps_more_history_and_unlimited_keeps_it_all()
    {
        var org = await NewOrg(Tiers.Free, domains: 1);
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await db.Assets.FirstAsync(a => a.TenantId == org.Id);
        foreach (var days in new[] { 200, 100, 50, 10, 1 })
            db.Results.Add(new CheckResult { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ObservedAt = now.AddDays(-days) });
        await db.SaveChangesAsync();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();

        await Edit(org.Id, t => t.OverrideRetentionMonths = 4);                       // about 120 days
        await retention.RunAsync(now);
        Assert.Equal(4, await db.Results.CountAsync(r => r.TenantId == org.Id));      // only the 200-day-old result went

        await Edit(org.Id, t => t.OverrideRetentionMonths = -1);
        await retention.RunAsync(now);
        Assert.Equal(4, await db.Results.CountAsync(r => r.TenantId == org.Id));
    }

    // ---- deleting a tenant ----

    async Task<int> RowsOf(Guid tenant)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AcrossTenants<User>().CountAsync(x => x.TenantId == tenant) + await db.AcrossTenants<Asset>().CountAsync(x => x.TenantId == tenant)
            + await db.AcrossTenants<CheckResult>().CountAsync(x => x.TenantId == tenant) + await db.AcrossTenants<Passkey>().CountAsync(x => x.TenantId == tenant)
            + await db.AcrossTenants<Invite>().CountAsync(x => x.TenantId == tenant) + await db.AcrossTenants<AlertSettings>().CountAsync(x => x.TenantId == tenant)
            + await db.AcrossTenants<AlertLogEntry>().CountAsync(x => x.TenantId == tenant) + await db.AcrossTenants<Change>().CountAsync(x => x.TenantId == tenant)
            + await db.AcrossTenants<CertificateRecord>().CountAsync(x => x.TenantId == tenant) + await db.AcrossTenants<Job>().CountAsync(x => x.TenantId == tenant)
            + await db.AcrossTenants<ExternalLogin>().CountAsync(x => x.TenantId == tenant) + await db.Tenants.CountAsync(x => x.Id == tenant);
    }

    /// <summary>Fills every table a tenant owns, so a deletion that forgets one shows up.</summary>
    async Task Populate(Org org)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = new Asset { TenantId = org.Id, Name = $"gone.{org.Id:N}.test.com", Kind = AssetKind.Domain };
        db.Assets.Add(asset);
        db.Results.Add(new CheckResult { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk" });
        db.Changes.Add(new Change { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns", Key = "k" });
        db.Certificates.Add(new CertificateRecord { TenantId = org.Id, AssetId = asset.Id, Source = "endpoint", Host = "h", Subject = "s", Issuer = "i", Fingerprint = Guid.NewGuid().ToString(), NotBefore = DateTimeOffset.UtcNow, NotAfter = DateTimeOffset.UtcNow.AddDays(9) });
        db.Jobs.Add(new Job { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns" });
        db.Passkeys.Add(new Passkey { TenantId = org.Id, UserId = org.Admin, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1], Name = "k" });
        db.ExternalLogins.Add(new ExternalLogin { TenantId = org.Id, UserId = org.Admin, Issuer = "https://idp.test", Subject = Guid.NewGuid().ToString() });
        db.Invites.Add(new Invite { TenantId = org.Id, TokenHash = Guid.NewGuid().ToByteArray(), CreatedBy = org.Admin, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        db.AlertSettings.Add(new AlertSettings { TenantId = org.Id, EmailTo = "x@y.test" });
        db.AlertLog.Add(new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "s", Success = true });
        await db.SaveChangesAsync();
    }

    [DbFact]
    public async Task The_tenant_admin_can_delete_their_own_organization_but_must_type_its_name()
    {
        var doomed = await NewOrg(); var bystander = await NewOrg();
        await Populate(doomed); await Populate(bystander);
        var before = await RowsOf(bystander.Id);
        var ann = factory.As(doomed.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/admin/tenant/delete", new { confirmName = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/admin/tenant/delete", new { confirmName = doomed.Name.ToLowerInvariant() })).StatusCode); // exact, case included
        Assert.True(await RowsOf(doomed.Id) > 10);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(doomed.Viewer).PostAsJsonAsync("/admin/tenant/delete", new { confirmName = doomed.Name })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ann.PostAsJsonAsync("/admin/tenant/delete", new { confirmName = doomed.Name })).StatusCode);
        Assert.Equal(0, await RowsOf(doomed.Id));
        Assert.Equal(before, await RowsOf(bystander.Id));                              // the other organization is untouched
    }

    [DbFact]
    public async Task The_owner_can_delete_any_customer_tenant_and_the_tenant_admin_cannot_delete_another()
    {
        var owner = await Owner();
        var doomed = await NewOrg(); var other = await NewOrg();
        await Populate(doomed);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.As(other.Admin).PostAsJsonAsync($"/platform/tenants/{doomed.Id}/delete", new { confirmName = doomed.Name })).StatusCode);
        Assert.True(await RowsOf(doomed.Id) > 10);

        var mia = factory.As(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await mia.PostAsJsonAsync($"/platform/tenants/{doomed.Id}/delete", new { confirmName = "nope" })).StatusCode);
        Assert.Contains("data-confirm-name", await (await mia.GetAsync($"/tenants/{doomed.Id}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await mia.PostAsJsonAsync($"/platform/tenants/{doomed.Id}/delete", new { confirmName = doomed.Name })).StatusCode);
        Assert.Equal(0, await RowsOf(doomed.Id));
        Assert.True(await RowsOf(other.Id) > 0);
        Assert.Equal(HttpStatusCode.NotFound, (await mia.PostAsJsonAsync($"/platform/tenants/{doomed.Id}/delete", new { confirmName = doomed.Name })).StatusCode); // already gone
        Assert.DoesNotContain(doomed.Name, await (await mia.GetAsync("/tenants")).Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task The_management_tenant_cannot_be_deleted_by_anyone()
    {
        var owner = await Owner();
        using var scope = Scope();
        var mgmt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants.SingleAsync(t => t.IsManagement);
        var mia = factory.As(owner);
        Assert.Equal(HttpStatusCode.Conflict, (await mia.PostAsJsonAsync($"/platform/tenants/{mgmt.Id}/delete", new { confirmName = mgmt.Name })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mia.PostAsJsonAsync("/admin/tenant/delete", new { confirmName = mgmt.Name })).StatusCode);
        var page = await (await mia.GetAsync($"/tenants/{mgmt.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-confirm-name", page);                              // no delete button offered
        Assert.DoesNotContain("data-confirm-name", await (await mia.GetAsync("/organization")).Content.ReadAsStringAsync());
        Assert.True(await RowsOf(mgmt.Id) > 0);
    }
}
