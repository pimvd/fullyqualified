using System.Net;
using System.Text.RegularExpressions;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class EffectiveStatusTests
{
    static CheckResult Dns(CheckStatus status, string issues, string summary = "Sum_DnsIssues|1") =>
        new() { AssetId = Guid.NewGuid(), CheckType = "dns", Status = status, Summary = summary, ValuesJson = $"{{\"_issues\":\"{issues}\"}}" };

    static CheckResult Tls(CheckStatus status, string summary) => new() { AssetId = Guid.NewGuid(), CheckType = "tls", Status = status, Summary = summary };

    [Fact]
    public void Muting_every_finding_of_a_check_makes_it_ok_but_one_left_over_still_counts()
    {
        var r = Dns(CheckStatus.Warning, "NoCaa,NoSpf");
        Assert.Equal(CheckStatus.Warning, Effective.Of(r, []).Status);
        Assert.Equal(CheckStatus.Warning, Effective.Of(r, ["NoCaa"]).Status);
        var all = Effective.Of(r, ["NoCaa", "NoSpf"]);
        Assert.Equal(CheckStatus.Ok, all.Status);
        Assert.Equal(["NoCaa", "NoSpf"], all.Silenced);
        Assert.Empty(all.Active);
    }

    [Fact]
    public void A_dangling_cname_stays_critical_until_it_is_muted_itself()
    {
        var r = Dns(CheckStatus.Critical, "DanglingCname,NoCaa");
        Assert.Equal(CheckStatus.Critical, Effective.Of(r, ["NoCaa"]).Status);
        Assert.Equal(CheckStatus.Warning, Effective.Of(r, ["DanglingCname"]).Status);
    }

    [Fact]
    public void An_optional_check_has_no_status_at_all()
    {
        var e = Effective.Of(Dns(CheckStatus.Warning, "NoCaa"), [CheckMute.WholeCheck]);
        Assert.Null(e.Status);
        Assert.True(e.Optional);
    }

    [Theory]
    [InlineData(CheckStatus.Critical, "Sum_TlsExpired|2026-01-01", "TlsExpired")]
    [InlineData(CheckStatus.Warning, "Sum_TlsOk|20|2026-12-01|R11", "TlsExpiring")]
    [InlineData(CheckStatus.Critical, "Sum_TlsOk|5|2026-12-01|R11", "TlsExpiring")]
    [InlineData(CheckStatus.Warning, "Sum_TlsChain|RemoteCertificateChainErrors", "TlsTrust")]
    [InlineData(CheckStatus.Warning, "Sum_TlsChainMismatch|x|h|n", "TlsTrust")]
    [InlineData(CheckStatus.Error, "Sum_Error|refused", "TlsError")]
    public void TLS_results_have_one_finding_that_can_be_muted(CheckStatus status, string summary, string code)
    {
        var r = Tls(status, summary);
        Assert.Equal([code], Effective.Codes(r));
        Assert.Equal(CheckStatus.Ok, Effective.Of(r, [code]).Status);
        Assert.Equal(status, Effective.Of(r, ["SomethingElse"]).Status);
    }

    [Fact]
    public void A_muted_certificate_silences_its_expiry_but_not_a_trust_problem()
    {
        Assert.Equal(CheckStatus.Ok, Effective.Of(Tls(CheckStatus.Warning, "Sum_TlsOk|20|x|y"), [], certMuted: true).Status);
        Assert.Equal(CheckStatus.Ok, Effective.Of(Tls(CheckStatus.Critical, "Sum_TlsExpired|x"), [], certMuted: true).Status);
        Assert.Equal(CheckStatus.Warning, Effective.Of(Tls(CheckStatus.Warning, "Sum_TlsChain|x"), [], certMuted: true).Status);
    }

    [Fact]
    public void Mutes_and_pauses_stop_counting_when_their_end_date_passes()
    {
        var now = DateTimeOffset.UtcNow;
        var asset = new Asset { Name = "a.example" };
        var result = Dns(CheckStatus.Warning, "NoCaa");
        result.AssetId = asset.Id;
        CheckMute Mute(DateTimeOffset? until) => new() { AssetId = asset.Id, CheckType = "dns", Code = "NoCaa", Until = until };

        Assert.Equal(CheckStatus.Ok, Board(asset, result, [Mute(null)], now).Worst);
        Assert.Equal(CheckStatus.Ok, Board(asset, result, [Mute(now.AddDays(3))], now).Worst);
        Assert.Equal(CheckStatus.Warning, Board(asset, result, [Mute(now.AddMinutes(-1))], now).Worst);

        asset.PausedAt = now.AddDays(-1);
        Assert.Null(Board(asset, result, [], now).Worst);                     // paused: counts for nothing
        Assert.False(Board(asset, result, [], now).NeedsAttention);
        asset.PausedUntil = now.AddDays(2);
        Assert.True(asset.IsPaused(now));
        asset.PausedUntil = now.AddSeconds(-1);
        Assert.False(asset.IsPaused(now));                                      // the end date passed: it is checked again by itself
        Assert.Equal(CheckStatus.Warning, Board(asset, result, [], now).Worst);
    }

    static AssetStatus Board(Asset a, CheckResult r, IReadOnlyCollection<CheckMute> mutes, DateTimeOffset now) =>
        StatusBoard.Build([a], [r], mutes, [], now)[a.Id];
}

public class PauseMuteTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    record Seed(Org Org, Asset Asset, CertificateRecord Cert);

    async Task<Seed> NewSeed()
    {
        var org = await NewOrg();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = new Asset { TenantId = org.Id, Name = $"mute.{org.Id:N}.test" };
        var other = new Asset { TenantId = org.Id, Name = $"ok.{org.Id:N}.test" };
        db.Assets.AddRange(asset, other);
        db.Results.AddRange(
            new CheckResult { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns", Status = CheckStatus.Warning, Summary = "Sum_DnsIssues|1", ValuesJson = "{\"_issues\":\"NoCaa\"}" },
            new CheckResult { TenantId = org.Id, AssetId = asset.Id, CheckType = "tls", Status = CheckStatus.Warning, Summary = "Sum_TlsOk|10|2030-01-01|R11" },
            new CheckResult { TenantId = org.Id, AssetId = other.Id, CheckType = "tls", Status = CheckStatus.Ok, Summary = "Sum_TlsOk|90|2030-01-01|R11" });
        var cert = new CertificateRecord { TenantId = org.Id, AssetId = asset.Id, Source = "endpoint", Host = asset.Name, Subject = "CN=x", Issuer = "R11", Fingerprint = "f" + org.Id.ToString("N"),
            NotBefore = DateTimeOffset.UtcNow.AddDays(-80), NotAfter = DateTimeOffset.UtcNow.AddDays(10) };
        db.Certificates.Add(cert);
        await db.SaveChangesAsync();
        return new Seed(org, asset, cert);
    }

    async Task<HttpResponseMessage> Post(HttpClient client, string tokenPage, string path, params (string, string)[] fields)
    {
        var page = await (await client.GetAsync(tokenPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        var response = await client.PostAsync(path, new FormUrlEncodedContent(form));
        // Say why when a form post is refused unexpectedly, so a failing run explains itself.
        if (response.StatusCode == HttpStatusCode.BadRequest && !fields.Any(f => f.Item1 == "until"))
            Console.Error.WriteLine($"POST {path} -> 400: {(await response.Content.ReadAsStringAsync())[..200]}");
        return response;
    }

    static string Tomorrow(int days = 1) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd");
    async Task<string> Dashboard(Guid user, string query = "") => await (await Factory.As(user).GetAsync("/" + query)).Content.ReadAsStringAsync();

    [DbFact]
    public async Task Pausing_stops_scans_and_resuming_looks_again()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        var page = $"/assets/{s.Asset.Id}";
        var paused = await Post(admin, page, $"{page}/pause", ("until", Tomorrow(5)), ("reason", "migrating"));
        Assert.Equal(HttpStatusCode.Redirect, paused.StatusCode);

        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            var asset = await db.Assets.SingleAsync(a => a.Id == s.Asset.Id);
            Assert.True(asset.IsPaused(DateTimeOffset.UtcNow));
            Assert.Equal("migrating", asset.PausedReason);

            await queue.EnqueueDueAsync(default);       // the scheduler skips it ...
            await queue.EnqueueAsync(asset);            // ... and so does a manual request
            Assert.False(await db.Jobs.AnyAsync(j => j.AssetId == s.Asset.Id));
        }

        Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, page, $"{page}/resume")).StatusCode);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await db.Assets.SingleAsync(a => a.Id == s.Asset.Id)).IsPaused(DateTimeOffset.UtcNow));
            Assert.True(await db.Jobs.AnyAsync(j => j.AssetId == s.Asset.Id));   // checked again right away
        }
    }

    [DbFact]
    public async Task A_paused_asset_is_not_a_problem_and_shows_as_paused()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        Assert.Contains(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));

        await Post(admin, $"/assets/{s.Asset.Id}", $"/assets/{s.Asset.Id}/pause", ("reason", "decommissioning"));
        Assert.DoesNotContain(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));
        var paused = await Dashboard(s.Org.Admin, "?status=paused");
        Assert.Contains(s.Asset.Name, paused);
        Assert.Contains("decommissioning", paused);
        Assert.DoesNotContain($"ok.{s.Org.Id:N}", paused);
    }

    [DbFact]
    public async Task Pausing_needs_a_future_date_and_an_editor()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        var page = $"/assets/{s.Asset.Id}";
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, page, $"{page}/pause", ("until", Tomorrow(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, page, $"{page}/pause", ("until", "not-a-date"))).StatusCode);

        var viewer = Factory.As(s.Org.Viewer);
        var response = await Post(viewer, "/", $"{page}/pause");
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        using var scope = Scope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Assets.SingleAsync(a => a.Id == s.Asset.Id)).IsPaused(DateTimeOffset.UtcNow));
    }

    [DbFact]
    public async Task A_muted_finding_stays_visible_but_stops_being_a_warning_until_its_end_date()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        var page = $"/assets/{s.Asset.Id}";
        // The TLS warning (expires in 10 days) is muted by a certificate mute below; here the DNS finding.
        await Post(admin, page, $"{page}/mute", ("check", "dns"), ("code", "NoCaa"), ("until", Tomorrow(3)));

        var detail = await (await admin.GetAsync(page)).Content.ReadAsStringAsync();
        Assert.Contains("No CAA record", detail);                   // still shown
        Assert.Contains("Unmute", detail);
        Assert.Contains("muted until", detail);                     // with its end date

        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(CheckStatus.Ok, StatusBoard.Build(await db.Assets.ToListAsync(), await db.LatestResultsAsync(), await db.Mutes.ToListAsync(), [], DateTimeOffset.UtcNow)
                [s.Asset.Id].Checks.Single(c => c.Result.CheckType == "dns").Effective.Status);

            // After the end date the finding counts again, without anyone doing anything.
            var mute = await db.Mutes.SingleAsync(m => m.AssetId == s.Asset.Id);
            mute.Until = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }
        Assert.Contains(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));
        Assert.Contains("No CAA record", await Dashboard(s.Org.Admin, "?status=problems"));

        // Unmuting removes it.
        var unmuted = await Post(admin, page, $"{page}/unmute", ("check", "dns"), ("code", "NoCaa"));
        Assert.Equal(HttpStatusCode.Redirect, unmuted.StatusCode);
        using var check = Scope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().Mutes.AnyAsync(m => m.AssetId == s.Asset.Id));
    }

    [DbFact]
    public async Task Making_a_check_optional_and_muting_a_certificate_clear_the_warnings()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        var page = $"/assets/{s.Asset.Id}";

        await Post(admin, page, $"{page}/mute", ("check", "dns"), ("code", "*"));            // DNS is optional for this asset
        var detail = await (await admin.GetAsync(page)).Content.ReadAsStringAsync();
        Assert.Contains("Optional", detail);
        Assert.Contains("Make required", detail);
        Assert.Contains(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));      // the TLS warning remains

        await Post(admin, page, $"/certificates/{s.Cert.Id}/mute", ("until", Tomorrow(30)));  // the certificate that is about to expire
        Assert.DoesNotContain(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));
        Assert.Contains("muted until", await (await admin.GetAsync(page)).Content.ReadAsStringAsync());

        await Post(admin, page, $"/certificates/{s.Cert.Id}/unmute");
        Assert.Contains(s.Asset.Name, await Dashboard(s.Org.Admin, "?status=problems"));
    }

    [DbFact]
    public async Task Selected_assets_can_be_paused_and_resumed_together()
    {
        var s = await NewSeed();
        var admin = Factory.As(s.Org.Admin);
        await Post(admin, "/", "/", ("_handler", "bulk-delete"), ("bulk", "pause"), ("ids", s.Asset.Id.ToString()));
        using (var scope = Scope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Assets.SingleAsync(a => a.Id == s.Asset.Id)).IsPaused(DateTimeOffset.UtcNow));
        await Post(admin, "/", "/", ("_handler", "bulk-delete"), ("bulk", "resume"), ("ids", s.Asset.Id.ToString()));
        using var after = Scope();
        Assert.False((await after.ServiceProvider.GetRequiredService<AppDbContext>().Assets.SingleAsync(a => a.Id == s.Asset.Id)).IsPaused(DateTimeOffset.UtcNow));
    }
}
