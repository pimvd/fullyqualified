using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class ReviewTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    record Seed(Org Org, Org Other, Asset Domain, ReviewHost A, ReviewHost B, ReviewHost C, ReviewHost Theirs);

    async Task<Seed> NewSeed()
    {
        var org = await NewOrg();
        var other = await NewOrg();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var domain = new Asset { TenantId = org.Id, Name = $"corp.{org.Id:N}.test" };
        var foreign = new Asset { TenantId = other.Id, Name = $"corp.{other.Id:N}.test" };
        db.Assets.AddRange(domain, foreign);
        ReviewHost Host(string name, Asset parent) => new() { TenantId = parent.TenantId, ParentAssetId = parent.Id, Name = $"{name}.{parent.Name}", Service = "crt.sh", Issuer = "R11", CertExpires = DateTimeOffset.UtcNow.AddDays(40) };
        var a = Host("api", domain); var b = Host("old", domain); var c = Host("mail", domain); var theirs = Host("api", foreign);
        db.ReviewHosts.AddRange(a, b, c, theirs);
        await db.SaveChangesAsync();
        return new Seed(org, other, domain, a, b, c, theirs);
    }

    async Task<HttpResponseMessage> Post(HttpClient client, string tokenPage, string path, params (string, string)[] fields)
    {
        var page = await (await client.GetAsync(tokenPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    async Task<ReviewHost> Load(Guid id)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ReviewHosts.SingleAsync(h => h.Id == id);
    }

    [DbFact]
    public async Task Certificate_log_search_runs_only_when_asked_never_by_the_scheduler()
    {
        var s = await NewSeed();
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            var domain = await db.Assets.SingleAsync(a => a.Id == s.Domain.Id);
            await queue.EnqueueAsync(domain);            // adding or resuming a domain
            await queue.EnqueueDueAsync(default);        // the scheduler
            var jobs = await db.Jobs.Where(j => j.AssetId == domain.Id).Select(j => j.CheckType).ToListAsync();
            Assert.Contains("dns", jobs);
            Assert.DoesNotContain("ct", jobs);
        }

        var admin = Factory.As(s.Org.Admin);
        var page = $"/assets/{s.Domain.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, page, $"{page}/discover")).StatusCode);
        await Post(admin, page, $"{page}/discover");                       // pressing twice does not queue twice
        using (var scope = Scope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.CountAsync(j => j.AssetId == s.Domain.Id && j.CheckType == "ct"));
        Assert.Contains("Find subdomains", await (await admin.GetAsync(page)).Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task A_certificate_log_result_is_optional_and_never_makes_an_asset_a_problem()
    {
        var failed = new CheckResult { AssetId = Guid.NewGuid(), CheckType = "ct", Status = CheckStatus.Error, Summary = "Sum_Error|crt.sh: 502" };
        var effective = Effective.Of(failed, []);
        Assert.Null(effective.Status);
        Assert.True(effective.Optional);
        var asset = new Asset { Name = "a.example" };
        failed.AssetId = asset.Id;
        var board = StatusBoard.Build([asset], [failed], [], [], DateTimeOffset.UtcNow)[asset.Id];
        Assert.False(board.NeedsAttention);
        Assert.Equal(0, board.MutedCount);   // and it does not show up as "1 muted" on every asset
        await Task.CompletedTask;
    }

    [DbFact]
    public async Task Ignoring_needs_a_reason_and_is_kept_with_who_and_when()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);

        var noReason = await Post(admin, "/review", "/review/ignore", ("ids", s.A.Id.ToString()), ("reason", "  "));
        Assert.Contains("error=reason", noReason.Headers.Location!.OriginalString);
        Assert.Equal(ReviewState.Pending, (await Load(s.A.Id)).State);

        var ok = await Post(admin, "/review", "/review/ignore", ("ids", s.A.Id.ToString()), ("ids", s.B.Id.ToString()), ("reason", "belongs to the partner, not us"));
        Assert.Contains("done=ignore:2", ok.Headers.Location!.OriginalString);
        var ignored = await Load(s.A.Id);
        Assert.Equal(ReviewState.Ignored, ignored.State);
        Assert.Equal("belongs to the partner, not us", ignored.Reason);
        Assert.NotNull(ignored.DecidedAt);

        var tab = await (await admin.GetAsync("/review?state=ignored")).Content.ReadAsStringAsync();
        Assert.Contains("belongs to the partner, not us", tab);
        Assert.Contains(s.A.Name, tab);
        Assert.DoesNotContain(s.C.Name, tab);                                             // still waiting, so not on this tab

        var restored = await Post(admin, "/review", "/review/restore", ("ids", s.A.Id.ToString()));
        Assert.Contains("done=restore:1", restored.Headers.Location!.OriginalString);
        var back = await Load(s.A.Id);
        Assert.Equal(ReviewState.Pending, back.State);
        Assert.Null(back.Reason);
    }

    [DbFact]
    public async Task Adding_creates_a_monitored_endpoint_that_remembers_where_it_came_from()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        var response = await Post(admin, "/review", "/review/add", ("ids", s.C.Id.ToString()));
        Assert.Contains("done=add:1:0", response.Headers.Location!.OriginalString);

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await db.Assets.SingleAsync(a => a.Name == s.C.Name);
        Assert.True(asset.Discovered);
        Assert.Equal(s.Domain.Id, asset.DiscoveredFromId);
        Assert.Equal("crt.sh", asset.DiscoveredVia);
        Assert.Equal(ReviewState.Added, (await db.ReviewHosts.SingleAsync(h => h.Id == s.C.Id)).State);
        Assert.Contains(await db.Jobs.Where(j => j.AssetId == asset.Id).Select(j => j.CheckType).ToListAsync(), t => t == "tls");   // monitored from now on
    }

    [DbFact]
    public async Task Review_decisions_cannot_reach_another_tenants_hostnames_and_viewers_cannot_decide()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        await Post(admin, "/review", "/review/ignore", ("ids", s.Theirs.Id.ToString()), ("reason", "not yours to decide"));
        await Post(admin, "/review", "/review/add", ("ids", s.Theirs.Id.ToString()));
        Assert.Equal(ReviewState.Pending, (await Load(s.Theirs.Id)).State);
        using (var scope = Scope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Assets.AnyAsync(a => a.Name == s.Theirs.Name));

        var viewer = Factory.As(s.Org.Viewer);
        var page = await (await viewer.GetAsync("/review")).Content.ReadAsStringAsync();
        Assert.Contains(s.A.Name, page);                                                   // they can read the list ...
        Assert.DoesNotContain("name=\"ids\"", page);                                       // ... but get no way to decide
        var attempt = await Post(viewer, "/", "/review/ignore", ("ids", s.A.Id.ToString()), ("reason", "x"));
        Assert.True(attempt.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, attempt.StatusCode.ToString());
        Assert.Equal(ReviewState.Pending, (await Load(s.A.Id)).State);
    }

    [DbFact]
    public async Task A_deleted_discovered_hostname_is_ignored_and_does_not_come_back()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        await Post(admin, "/review", "/review/add", ("ids", s.A.Id.ToString()));
        Guid assetId;
        using (var scope = Scope())
            assetId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Assets.SingleAsync(a => a.Name == s.A.Name)).Id;

        await Post(admin, $"/assets/{assetId}", $"/assets/{assetId}", ("_handler", "delete"));

        var host = await Load(s.A.Id);
        Assert.Equal(ReviewState.Ignored, host.State);
        Assert.Equal(ReviewHost.RemovedReason, host.Reason);

        // The next search finds it again, and nothing changes.
        using var scope2 = Scope();
        var runner = scope2.ServiceProvider.GetRequiredService<CheckRunner>();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var domain = await db.Assets.SingleAsync(a => a.Id == s.Domain.Id);
        var found = new List<DiscoveredHost> { new(s.A.Name, "crt.sh", "R11", DateTimeOffset.UtcNow.AddDays(30)) };
        await runner.ExecuteAsync(domain, new FakeCt(found), default);
        Assert.False(await db.Assets.AnyAsync(a => a.Name == s.A.Name));
        Assert.Equal(ReviewState.Ignored, (await db.ReviewHosts.SingleAsync(h => h.Id == s.A.Id)).State);

        var tab = await (await admin.GetAsync("/review?state=ignored")).Content.ReadAsStringAsync();
        Assert.Contains("Removed from monitoring", tab);
    }

    [DbFact]
    public async Task The_dashboard_points_at_what_waits_for_review()
    {
        var s = await NewSeed();
        var page = await (await Factory.As(s.Org.Admin).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("3 hostname(s) found in the certificate logs are waiting for your review.", page);
        Assert.Contains("href=\"/review\"", page);
    }

    [DbFact]
    public async Task The_reports_list_every_decision_with_its_reason()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        await Post(admin, "/review", "/review/ignore", ("ids", s.B.Id.ToString()), ("reason", "decommissioned in 2024"));
        await Post(admin, "/review", "/review/add", ("ids", s.A.Id.ToString()));

        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Id = s.Org.Id;
        var model = await scope.ServiceProvider.GetRequiredService<ReportBuilder>().BuildAsync();
        Assert.Equal(3, model.ReviewRows.Count);                                          // only this tenant's
        Assert.Contains(model.ReviewRows, r => r.Host == s.B.Name && r.State == ReviewState.Ignored && r.Reason == "decommissioned in 2024");
        Assert.Contains(model.ReviewRows, r => r.Host == s.A.Name && r.State == ReviewState.Added);
        Assert.Contains(model.ReviewRows, r => r.Host == s.C.Name && r.State == ReviewState.Pending);

        var l = new KeyLocalizer();
        var xlsx = ReportWriter.Excel(model, l);
        using var book = new XLWorkbook(new MemoryStream(xlsx));
        var sheet = book.Worksheet("Rep_ReviewTitle");
        var rows = sheet.RowsUsed().Skip(1).Select(r => (Host: r.Cell(1).GetString(), Reason: r.Cell(5).GetString())).ToList();
        Assert.Contains((s.B.Name, "decommissioned in 2024"), rows);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ReportWriter.Pdf(model, l), 0, 4));

        // The real downloads work, with the real texts.
        var download = await admin.GetAsync("/reports/nis2.xlsx");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var real = new XLWorkbook(new MemoryStream(await download.Content.ReadAsByteArrayAsync()));
        Assert.Contains(real.Worksheets, w => w.Name == "Discovery review");
    }

    [DbFact]
    public async Task The_domain_controls_list_the_assets_with_zone_values_and_the_asset_count_is_every_asset()
    {
        var org = await NewOrg();
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var apex = new Asset { TenantId = org.Id, Name = $"zone.{org.Id:N}.test" };
            var host = new Asset { TenantId = org.Id, Name = $"www.zone.{org.Id:N}.test" };
            var port = new Asset { TenantId = org.Id, Name = $"zone.{org.Id:N}.test:8443" };
            db.Assets.AddRange(apex, host, port);
            CheckResult Dns(Asset a, Dictionary<string, string> values) => new() { TenantId = org.Id, AssetId = a.Id, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ValuesJson = System.Text.Json.JsonSerializer.Serialize(values) };
            db.Results.AddRange(
                Dns(apex, new() { ["spf"] = "v=spf1 -all", ["ns"] = "ns1.example", ["dnssec"] = "yes" }),
                Dns(host, new() { ["spf"] = "v=spf1 -all", ["a"] = "192.0.2.1" }));
            await db.SaveChangesAsync();
        }

        using var read = Factory.Services.CreateScope();
        read.ServiceProvider.GetRequiredService<TenantScope>().Id = org.Id;
        var model = await read.ServiceProvider.GetRequiredService<ReportBuilder>().BuildAsync();
        var row = Assert.Single(model.Domains);                                          // the host and the endpoint have no zone values
        Assert.Equal($"zone.{org.Id:N}.test", row.Domain);
        Assert.True(row.Spf); Assert.True(row.Dnssec); Assert.False(row.Caa);
        Assert.Equal(3, model.Assets);
    }

    sealed class FakeCt(List<DiscoveredHost> found) : ICheck
    {
        public string Type => "ct";
        public bool Automatic => false;
        public bool AppliesTo(Asset asset) => true;
        public Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct) => Task.FromResult(new CheckOutcome(CheckStatus.Ok, "Sum_CtOk|0|1", [], null, found));
    }
}
