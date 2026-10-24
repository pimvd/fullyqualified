using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class DashboardTests : IClassFixture<PgFactory>
{
    readonly PgFactory factory;
    static readonly SemaphoreSlim SeedOnce = new(1, 1);
    static Guid? seeded;

    public DashboardTests(PgFactory factory) => this.factory = factory;

    /// <summary>Every test gets a new instance but shares the fixture, so the data is created once.</summary>
    Task<Guid> admin => AdminAsync();

    async Task<Guid> AdminAsync()
    {
        await SeedOnce.WaitAsync();
        try { return seeded ??= await Seed(); } finally { SeedOnce.Release(); }
    }

    async Task<Guid> Seed()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var tenant = new Tenant { Name = "Dash" };
        db.Tenants.Add(tenant);
        var dee = new User { TenantId = tenant.Id, Name = "Dee", Role = Role.Admin, Email = "dee@dash.test" };
        db.Users.Add(dee);

        var manual = new Asset { TenantId = tenant.Id, Name = "dash.example", Kind = AssetKind.Domain };
        var found = new Asset
        {
            TenantId = tenant.Id, Name = "api.dash.example", Kind = AssetKind.Endpoint, Discovered = true, DiscoveredVia = "Cert Spotter",
            DiscoveredFromId = manual.Id, DiscoveredIssuer = "Issuer Seven", DiscoveredCertExpires = DateTimeOffset.UtcNow.AddDays(40),
        };
        var legacy = new Asset { TenantId = tenant.Id, Name = "old.dash.example", Kind = AssetKind.Endpoint, Discovered = true };
        var fine = new Asset { TenantId = tenant.Id, Name = "fine.dash.example", Kind = AssetKind.Endpoint, Discovered = true, DiscoveredVia = "crt.sh", DiscoveredFromId = manual.Id };
        db.Assets.AddRange(manual, found, legacy, fine);

        CheckResult R(Asset a, string type, CheckStatus s, string summary, string values = "{}") =>
            new() { TenantId = tenant.Id, AssetId = a.Id, CheckType = type, Status = s, Summary = summary, ValuesJson = values };
        db.Results.AddRange(
            R(manual, "dns", CheckStatus.Warning, "Sum_DnsIssues|2", "{\"_issues\":\"NoSpf,DkimMissing:s1\",\"dkim.protonmail3\":\"2048-bit key abc\",\"dkim\":\"selector1\",\"dmarc\":\"v=DMARC1; p=none\",\"mx\":\"10 mail.example.net\"}"),
            R(found, "tls", CheckStatus.Warning, "Sum_TlsChain|RemoteCertificateNameMismatch"),
            R(legacy, "tls", CheckStatus.Critical, "Sum_TlsExpired|2026-01-01"),
            R(fine, "tls", CheckStatus.Ok, "Sum_TlsOk|90|2027-01-01|R11"));
        db.Changes.Add(new Change { TenantId = tenant.Id, AssetId = manual.Id, CheckType = "dns", Key = "dkim.protonmail3", Before = "", After = "2048-bit key abc" });
        await db.SaveChangesAsync();
        return dee.Id;
    }

    async Task<string> Page(string query) => await (await factory.As(await admin).GetAsync("/" + query)).Content.ReadAsStringAsync();

    [DbFact]
    public async Task Every_asset_says_whether_it_was_added_or_discovered_and_why_it_is_not_ok()
    {
        var page = await Page("");
        Assert.Contains("Added manually", page);
        Assert.Contains("Discovered", page);
        Assert.Contains("via Cert Spotter", page);
        Assert.Contains("from dash.example", page);
        Assert.Contains("No SPF record", page);                              // the reasons from the DNS check ...
        Assert.Contains("DKIM selector &#x27;s1&#x27; not found", page);
        Assert.Contains("RemoteCertificateNameMismatch", page);              // ... and from the TLS check, in the same table
        Assert.Contains("Showing 4 of 4", page);
    }

    [DbFact]
    public async Task The_origin_filter_separates_manual_from_discovered_assets()
    {
        var added = await Page("?source=added");
        Assert.Contains("dash.example</a>", added);
        Assert.DoesNotContain("api.dash.example", added);
        Assert.Contains("Showing 1 of 4", added);

        var discovered = await Page("?source=discovered");
        Assert.DoesNotContain(">dash.example</a>", discovered);
        Assert.Contains("api.dash.example", discovered);
        Assert.Contains("Showing 3 of 4", discovered);
    }

    [DbFact]
    public async Task The_status_type_and_search_filters_combine_and_problems_come_first()
    {
        var problems = await Page("?status=problems");
        Assert.DoesNotContain("fine.dash.example", problems);
        Assert.Contains("Showing 3 of 4", problems);
        Assert.True(problems.IndexOf("old.dash.example") < problems.IndexOf("api.dash.example")); // critical before warning

        var critical = await Page("?status=critical&type=endpoint&q=OLD");
        Assert.Contains("old.dash.example", critical);
        Assert.Contains("Showing 1 of 4", critical);

        var none = await Page("?q=nothing-like-this");
        Assert.Contains("Nothing matches these filters.", none);
    }

    [DbFact]
    public async Task A_discovered_asset_explains_how_it_was_found_and_older_ones_fall_back_gracefully()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var found = await db.Assets.SingleAsync(a => a.Name == "api.dash.example");
        var legacy = await db.Assets.SingleAsync(a => a.Name == "old.dash.example");

        var detail = await (await factory.As(await admin).GetAsync($"/assets/{found.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("How it was discovered", detail);
        Assert.Contains("Cert Spotter", detail);
        Assert.Contains("Issuer Seven", detail);
        Assert.Contains("dash.example", detail);

        var old = await (await factory.As(await admin).GetAsync($"/assets/{legacy.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("added before the details were recorded", old);
        Assert.Contains("/assets/", old); // the parent is inferred from the name
    }

    [DbFact]
    public async Task DNS_value_names_open_the_lookup_for_the_right_record()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        var manual = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Assets.SingleAsync(a => a.Name == "dash.example");

        var page = await (await factory.As(await admin).GetAsync($"/assets/{manual.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("data-dig-name=\"protonmail3._domainkey.dash.example\" data-dig-as=\"TXT\"", page);   // dkim.<selector>
        Assert.Contains("data-dig-name=\"_dmarc.dash.example\" data-dig-as=\"TXT\"", page);                    // dmarc
        Assert.Contains("data-dig-name=\"dash.example\" data-dig-as=\"MX\"", page);                            // mx
        Assert.Contains("data-dig-name=\"selector1._domainkey.dash.example\" data-dig-as=\"TXT\"", page);      // a selector found by trying common names

        var changes = await (await factory.As(await admin).GetAsync("/changes")).Content.ReadAsStringAsync();
        Assert.Contains("data-dig-name=\"protonmail3._domainkey.dash.example\"", changes);                       // the change log links too
    }
}
