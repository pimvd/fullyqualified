using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullyQualified.Tests;

/// <summary>Alarm rules against a real database: who may manage them, what they accept, and when a check run alarms because of one.</summary>
public class AlertRuleDbTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    sealed class FakeDns(Dictionary<string, string> values) : ICheck
    {
        public string Type => "dns";
        public bool AppliesTo(Asset asset) => true;
        public Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct) => Task.FromResult(new CheckOutcome(CheckStatus.Ok, "Sum_DnsOk", values));
    }

    static Dictionary<string, string> Dns(string ns = "ns1.example; ns2.example", string mx = "10 mail.example") => new() { ["ns"] = ns, ["mx"] = mx, ["spf"] = "v=spf1 -all" };

    /// <summary>An organization whose alarms only fire for what the test asks: no drift alarm, no recovery, a webhook on a closed port so each attempt is logged.</summary>
    async Task<Org> OrgWithChannel(bool drift = false)
    {
        var org = await NewOrg();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        db.AlertSettings.Add(new AlertSettings { TenantId = org.Id, WebhookUrl = alerts.Protect("http://127.0.0.1:1/closed"), NotifyDrift = drift, NotifyRecovery = false });
        await db.SaveChangesAsync();
        return org;
    }

    async Task Rule(Guid tenant, string type, string key, Guid? asset = null)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AlertRules.Add(new AlertRule { TenantId = tenant, CheckType = type, Key = key, AssetId = asset });
        await db.SaveChangesAsync();
    }

    async Task Run(Guid assetId, Dictionary<string, string> values)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await db.Assets.SingleAsync(a => a.Id == assetId);
        await scope.ServiceProvider.GetRequiredService<CheckRunner>().ExecuteAsync(asset, new FakeDns(values), default);
    }

    async Task<List<string>> Alarms(Guid tenant)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<AlertLogEntry>().Where(a => a.TenantId == tenant).Select(a => a.Subject).ToListAsync();
    }

    // ---- managing rules ----

    [DbFact]
    public async Task An_admin_adds_and_removes_rules_and_nobody_else_can()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        var created = await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "ns", assetId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();
        Assert.Contains("Name servers (NS)", await (await admin.GetAsync("/alerts")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Viewer).PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Viewer).DeleteAsync($"/admin/alerts/rules/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/admin/alerts/rules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/admin/alerts/rules/{id}")).StatusCode);
    }

    [DbFact]
    public async Task Rules_must_name_a_real_value_and_may_not_repeat()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "nonsense" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "ct", key = "*" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "issuer" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx" })).StatusCode);
        var again = await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx" });   // "every asset" twice
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("rule_exists", await again.Content.ReadAsStringAsync());

        var asset = await AddAsset(org.Id, $"one.{org.Id:N}.test.com");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx", assetId = asset.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "mx", assetId = asset.Id })).StatusCode);
    }

    [DbFact]
    public async Task An_organization_cannot_use_or_remove_the_rules_or_assets_of_another()
    {
        var acme = await NewOrg();
        var globex = await NewOrg();
        var theirAsset = await AddAsset(globex.Id, $"g.{globex.Id:N}.test.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.As(acme.Admin).PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "ns", assetId = theirAsset.Id })).StatusCode);

        var theirs = await (await Factory.As(globex.Admin).PostAsJsonAsync("/admin/alerts/rules", new { checkType = "dns", key = "ns" })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.As(acme.Admin).DeleteAsync($"/admin/alerts/rules/{theirs.GetProperty("id").GetGuid()}")).StatusCode);
    }

    // ---- what a rule does to alarms ----

    [DbFact]
    public async Task A_rule_alarms_on_the_change_it_asks_about_and_on_nothing_else()
    {
        var org = await OrgWithChannel();
        var asset = await AddAsset(org.Id, $"r.{org.Id:N}.test.com");
        await Rule(org.Id, "dns", "ns");

        await Run(asset.Id, Dns());                                              // first run: a baseline, never an alarm
        Assert.Empty(await Alarms(org.Id));

        await Run(asset.Id, Dns(mx: "10 other.example"));                        // mx changed, nobody asked about mx
        Assert.Empty(await Alarms(org.Id));

        await Run(asset.Id, Dns(ns: "ns9.example", mx: "10 other.example"));     // ns changed
        var alarms = await Alarms(org.Id);
        Assert.Single(alarms);
        Assert.Contains("changed: ns", alarms[0]);
    }

    [DbFact]
    public async Task Without_a_rule_the_same_change_is_silent_when_the_drift_alarm_is_off()
    {
        var org = await OrgWithChannel();
        var asset = await AddAsset(org.Id, $"q.{org.Id:N}.test.com");
        await Run(asset.Id, Dns());
        await Run(asset.Id, Dns(ns: "ns9.example"));
        Assert.Empty(await Alarms(org.Id));
    }

    [DbFact]
    public async Task A_rule_for_one_asset_ignores_the_changes_of_the_others()
    {
        var org = await OrgWithChannel();
        var watched = await AddAsset(org.Id, $"w.{org.Id:N}.test.com");
        var other = await AddAsset(org.Id, $"o.{org.Id:N}.test.com");
        await Rule(org.Id, "dns", "mx", watched.Id);
        foreach (var a in new[] { watched, other }) await Run(a.Id, Dns());

        await Run(other.Id, Dns(mx: "10 new.example"));
        Assert.Empty(await Alarms(org.Id));
        await Run(watched.Id, Dns(mx: "10 new.example"));
        Assert.Single(await Alarms(org.Id));
    }

    [DbFact]
    public async Task Any_change_rules_and_the_drift_alarm_together_send_one_alarm_not_two()
    {
        var org = await OrgWithChannel(drift: true);
        var asset = await AddAsset(org.Id, $"d.{org.Id:N}.test.com");
        await Rule(org.Id, "dns", "*");
        await Run(asset.Id, Dns());
        await Run(asset.Id, Dns(ns: "ns9.example", mx: "10 new.example"));
        var alarms = await Alarms(org.Id);
        Assert.Single(alarms);
        Assert.Contains("changed: mx, ns", alarms[0]);   // changes come sorted by value name
    }

    [DbFact]
    public async Task A_rule_of_one_organization_never_alarms_another()
    {
        var acme = await OrgWithChannel();
        var globex = await OrgWithChannel();
        var asset = await AddAsset(globex.Id, $"x.{globex.Id:N}.test.com");
        await Rule(acme.Id, "dns", "*");
        await Run(asset.Id, Dns());
        await Run(asset.Id, Dns(ns: "ns9.example"));
        Assert.Empty(await Alarms(globex.Id));
        Assert.Empty(await Alarms(acme.Id));
    }
    // ---- the alarm button on the asset page ----

    async Task<HttpResponseMessage> PostForm(HttpClient client, string tokenPage, string path, params (string, string)[] fields)
    {
        var page = await (await client.GetAsync(tokenPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    async Task<Asset> AssetWithDns(Guid tenant)
    {
        var asset = await AddAsset(tenant, $"p.{tenant:N}.test.com");
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Results.Add(new CheckResult { TenantId = tenant, AssetId = asset.Id, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ValuesJson = "{\"ns\":\"ns1.example\",\"mx\":\"10 mail.example\"}" });
        await db.SaveChangesAsync();
        return asset;
    }

    async Task<List<AlertRule>> RulesOf(Guid tenant)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<AlertRule>().Where(r => r.TenantId == tenant).ToListAsync();
    }

    [DbFact]
    public async Task An_admin_turns_an_alarm_on_and_off_for_one_dns_value_from_the_asset_page()
    {
        var org = await NewOrg();
        var asset = await AssetWithDns(org.Id);
        var admin = Factory.As(org.Admin);
        var page = $"/assets/{asset.Id}";

        Assert.Contains("Alarm on change", await admin.GetStringAsync(page));
        var on = await PostForm(admin, page, $"{page}/alarm", ("check", "dns"), ("key", "ns"));
        Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        var rule = Assert.Single(await RulesOf(org.Id));
        Assert.Equal(("dns", "ns", asset.Id), (rule.CheckType, rule.Key, rule.AssetId));
        Assert.Contains("Alarm on<", await admin.GetStringAsync(page));

        await PostForm(admin, page, $"{page}/alarm", ("check", "dns"), ("key", "ns"));        // pressing it twice adds nothing
        Assert.Single(await RulesOf(org.Id));

        Assert.Equal(HttpStatusCode.Redirect, (await PostForm(admin, page, $"{page}/unalarm", ("check", "dns"), ("key", "ns"))).StatusCode);
        Assert.Empty(await RulesOf(org.Id));
    }

    [DbFact]
    public async Task The_alarm_button_is_for_admins_and_refuses_unknown_values_and_other_organizations()
    {
        var org = await NewOrg();
        var other = await NewOrg();
        var asset = await AssetWithDns(org.Id);
        var theirs = await AssetWithDns(other.Id);
        var page = $"/assets/{asset.Id}";

        var viewer = Factory.As(org.Viewer);
        Assert.DoesNotContain("Alarm on change", await viewer.GetStringAsync(page));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostForm(viewer, "/", $"{page}/alarm", ("check", "dns"), ("key", "ns"))).StatusCode);

        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostForm(admin, page, $"{page}/alarm", ("check", "dns"), ("key", "bogus"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostForm(admin, page, $"/assets/{theirs.Id}/alarm", ("check", "dns"), ("key", "ns"))).StatusCode);
        Assert.Empty(await RulesOf(org.Id));
        Assert.Empty(await RulesOf(other.Id));
    }

    [DbFact]
    public async Task A_rule_for_every_asset_is_shown_on_the_asset_page_instead_of_a_button()
    {
        var org = await NewOrg();
        var asset = await AssetWithDns(org.Id);
        await Rule(org.Id, "dns", "ns");
        var page = await Factory.As(org.Admin).GetStringAsync($"/assets/{asset.Id}");
        Assert.Contains("Alarm on for every asset", page);
        Assert.Single(Regex.Matches(page, "Alarm on change"));       // only the mail servers still offer the button
    }
}
