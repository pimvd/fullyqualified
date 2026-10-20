using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class PlanLogicTests
{
    static Tier Tier(int? domains = 3, int? endpoints = 10, int? users = 2, bool reports = false, int? months = 1) =>
        new() { Key = "t", Name = "T", MaxDomains = domains, MaxEndpoints = endpoints, MaxUsers = users, ReportsEnabled = reports, RetentionMonths = months };

    [Fact]
    public void A_tenant_follows_its_tier_unless_it_overrides()
    {
        var tenant = new Tenant { Name = "x" };
        Assert.Equal(new PlanLimits(3, 10, 2, false, 1), PlanLimits.Resolve(Tier(), tenant));

        tenant.OverrideMaxDomains = 7; tenant.OverrideReports = true; tenant.OverrideRetentionMonths = 6;
        Assert.Equal(new PlanLimits(7, 10, 2, true, 6), PlanLimits.Resolve(Tier(), tenant));

        tenant.OverrideMaxUsers = -1; tenant.OverrideMaxEndpoints = 0;       // -1 lifts the limit, 0 is a real limit
        tenant.OverrideRetentionMonths = -1;                                  // keep everything
        Assert.Equal(new PlanLimits(7, 0, null, true, null), PlanLimits.Resolve(Tier(), tenant));

        tenant.OverrideReports = false;                                       // an override can also take something away
        Assert.False(PlanLimits.Resolve(Tier(reports: true), tenant).ReportsEnabled);
    }

    [Fact]
    public void A_tier_with_no_numbers_is_unlimited()
    {
        var limits = PlanLimits.Resolve(Tier(null, null, null, true, null), new Tenant { Name = "x" });
        Assert.Equal(new PlanLimits(null, null, null, true, null), limits);
    }

    static Plan PlanWith(PlanLimits limits, PlanUsage usage) => new(new Tenant { Name = "x" }, Tier(), limits, usage);

    [Fact]
    public void Adding_an_asset_is_refused_at_the_limit_and_only_for_that_kind()
    {
        var plan = PlanWith(new(3, 10, 5, false, 1), new(3, 4, 1, 0));
        Assert.Equal("Limit_Domains", plan.Blocks(AssetKind.Domain));
        Assert.Null(plan.Blocks(AssetKind.Endpoint));
        Assert.Equal(6, plan.EndpointsLeft);
        Assert.Null(PlanWith(new(null, null, null, false, null), new(500, 500, 500, 0)).Blocks(AssetKind.Domain));
        Assert.Null(PlanWith(new(null, null, null, false, null), new(0, 0, 0, 0)).EndpointsLeft);
    }

    [Fact]
    public void Open_invitations_count_towards_the_user_limit_but_redeeming_one_does_not_count_it_twice()
    {
        var plan = PlanWith(new(1, 1, 3, false, 1), new(0, 0, 2, 1));
        Assert.False(plan.HasRoomForUser());                       // 2 users + 1 invitation = 3 of 3
        Assert.True(plan.HasRoomForUser(redeemingInvite: true));   // the invitation being redeemed is that third seat
        Assert.True(PlanWith(new(1, 1, 3, false, 1), new(0, 0, 2, 0)).HasRoomForUser());
        Assert.False(PlanWith(new(1, 1, 2, false, 1), new(0, 0, 2, 0)).HasRoomForUser(redeemingInvite: true));
    }

    [Fact]
    public void The_default_tiers_are_the_plans_in_the_product_plan()
    {
        Assert.Equal(["free", "starter", "business", "custom"], Tiers.Defaults.OrderBy(t => t.SortOrder).Select(t => t.Key));
        var root = new SiteFixture().Root;
        var plan = File.ReadAllText(Path.Combine(root, "docs", "PLAN.md"));
        foreach (var m in Regex.Matches(plan, @"^\| (Free|Starter|Business) \| (\d+) \| (\d+) months? \|", RegexOptions.Multiline).Cast<Match>())
        {
            var tier = Tiers.Defaults.Single(t => t.Name == m.Groups[1].Value);
            Assert.Equal((int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)), (tier.MaxDomains, tier.RetentionMonths));
        }
        Assert.Equal(3, Regex.Matches(plan, @"^\| (Free|Starter|Business) \| \d+ \| \d+ months? \|", RegexOptions.Multiline).Count);
        Assert.True(Tiers.Defaults.Single(t => t.Key == "custom") is { MaxDomains: null, MaxUsers: null, RetentionMonths: null });
        Assert.False(Tiers.Defaults.Single(t => t.Key == "free").ReportsEnabled);
        Assert.True(Tiers.Defaults.Where(t => t.Key != "free").All(t => t.ReportsEnabled));
    }

    [Fact]
    public void The_migration_seeds_the_same_values_as_the_code()
    {
        var sql = Directory.GetFiles(Path.Combine(new SiteFixture().Root, "src", "FullyQualified.Web", "Data", "Migrations"), "*_Plans.cs").Select(File.ReadAllText).Single();
        foreach (var t in Tiers.Defaults)
        {
            string N(int? n) => n?.ToString() ?? "NULL";
            var row = $"('{t.Key}',";
            Assert.Contains(row, sql);
            Assert.Matches($@"\('{t.Key}',\s*'{t.Name}',\s*{t.SortOrder},\s*{N(t.MaxDomains)},\s*{N(t.MaxEndpoints)},\s*{N(t.MaxUsers)},\s*{t.ReportsEnabled.ToString().ToLowerInvariant()},\s*{N(t.RetentionMonths)},", sql);
        }
    }
}

public class PublicScanTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  HTTPS://Www.Example.COM/path?x=1 ", "www.example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("example.com:8443", null)]       // a port means a service the visitor picked: not allowed
    [InlineData("127.0.0.1", null)]
    [InlineData("93.184.216.34", null)]           // addresses are not names
    [InlineData("1.2.3.44", null)]
    [InlineData("[::1]", null)]
    [InlineData("localhost", null)]
    [InlineData("printer.local", null)]
    [InlineData("intranet.corp", null)]
    [InlineData("router.lan", null)]
    [InlineData("host.internal", null)]
    [InlineData("x.example", null)]
    [InlineData("", null)]
    [InlineData("not a host", null)]
    public void Only_public_host_names_are_accepted(string input, string? expected) => Assert.Equal(expected, PublicScanService.Normalize(input));

    static ScanThrottle Throttle(int perHour = 2, int concurrent = 1) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["PublicScan:PerHour"] = perHour.ToString(), ["PublicScan:Concurrent"] = concurrent.ToString() }).Build());

    [Fact]
    public void One_address_may_scan_a_few_times_an_hour()
    {
        var throttle = Throttle(perHour: 2);
        var t0 = DateTimeOffset.UtcNow;
        Assert.True(throttle.TryAcquire("1.1.1.1", t0));
        Assert.True(throttle.TryAcquire("1.1.1.1", t0.AddMinutes(1)));
        Assert.False(throttle.TryAcquire("1.1.1.1", t0.AddMinutes(2)));
        Assert.True(throttle.TryAcquire("2.2.2.2", t0.AddMinutes(2)));          // someone else is unaffected
        Assert.True(throttle.TryAcquire("1.1.1.1", t0.AddMinutes(61)));         // the window slides
    }

    [Fact]
    public void Only_so_many_scans_run_at_once()
    {
        var throttle = Throttle(concurrent: 2);
        var a = throttle.TryEnter(); var b = throttle.TryEnter();
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Null(throttle.TryEnter());
        a!.Dispose();
        Assert.NotNull(throttle.TryEnter());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // cloud metadata
    [InlineData("100.64.0.1")]
    [InlineData("192.0.0.8")]
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public async Task The_strict_guard_refuses_private_and_local_addresses_whatever_the_configuration(string address)
    {
        var result = await HostGuard.Strict().ResolveAsync(address, default);
        Assert.False(result.Allowed);
        Assert.Null(result.Address);
    }

    [Fact]
    public async Task A_public_address_is_pinned_so_the_connection_goes_to_what_was_checked()
    {
        var result = await HostGuard.Strict().ResolveAsync("93.184.216.34", default);
        Assert.True(result.Allowed);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), result.Address);
        Assert.Null((await new HostGuard(true).ResolveAsync("127.0.0.1", default)).Address); // a private-friendly install connects by name as before
    }

    [Fact]
    public async Task A_public_scan_never_connects_to_a_private_address()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var outcome = await new TlsCheck(HostGuard.Strict()).RunAsync(new Asset { Name = $"127.0.0.1:{port}", Kind = AssetKind.Endpoint }, default);
            Assert.Equal("Sum_Blocked", outcome.Summary);
            Assert.False(listener.Pending(), "the scanner must not even have tried to connect");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task The_scanner_is_off_by_default_and_refuses_bad_input_before_touching_the_network()
    {
        var off = new PublicScanService(new ConfigurationBuilder().Build(), Throttle(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicScanService>.Instance);
        Assert.False(off.Enabled);
        Assert.Equal("disabled", (await off.ScanAsync("example.com", null, default)).Error);

        var on = new PublicScanService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PublicScan:Enabled"] = "true" }).Build(), Throttle(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicScanService>.Instance);
        foreach (var bad in new[] { "127.0.0.1", "localhost", "example.com:22", "", "10.0.0.1", "a b" })
            Assert.Equal("invalid", (await on.ScanAsync(bad, IPAddress.Loopback, default)).Error);
    }
}

public class PublicScanPageFactory(bool enabled) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:AutoMigrate", "false");
        builder.UseSetting("Scanning:Enabled", "false");
        if (enabled) builder.UseSetting("PublicScan:Enabled", "true");
    }
}

public class PublicScanPageTests
{
    [Fact]
    public async Task The_public_page_does_not_exist_unless_it_is_switched_on()
    {
        using var factory = new PublicScanPageFactory(false);
        var response = await factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/scan?domain=example.com");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("name=\"domain\"", await response.Content.ReadAsStringAsync()); // nothing to type into when it is off
    }

    [Fact]
    public async Task When_it_is_on_anyone_can_open_it_without_signing_in_and_bad_input_gets_an_explanation()
    {
        using var factory = new PublicScanPageFactory(true);
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = await client.GetAsync("/scan");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var html = await form.Content.ReadAsStringAsync();
        Assert.Contains("name=\"domain\"", html);
        Assert.Contains("method=\"get\"", html);
        Assert.Contains("noindex", html);
        Assert.False(form.Headers.Contains("Set-Cookie"));

        var bad = await (await client.GetAsync("/scan?domain=192.168.0.1")).Content.ReadAsStringAsync();
        Assert.Contains("Enter a public domain name", bad);
        Assert.DoesNotContain("Free certificate and DNS check</h2>", bad);
        var dutch = await (await client.GetAsync("/scan?domain=localhost&culture=nl&ui-culture=nl")).Content.ReadAsStringAsync();
        Assert.Contains("Vul een openbare domeinnaam in", dutch);
    }
}
