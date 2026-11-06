using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

/// <summary>
/// RetentionService against a real database. It deletes history for every tenant in the database, so each test seeds its own
/// tenants and asserts on their rows; the returned counts are checked as a lower bound because other tests' tenants may be swept too.
/// </summary>
public class RetentionTests : IClassFixture<PgFactory>
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    readonly PgFactory factory;
    public RetentionTests(PgFactory factory) => this.factory = factory;

    IServiceScope Scope() { var s = factory.Services.CreateScope(); s.ServiceProvider.GetRequiredService<TenantScope>().All = true; return s; }

    record Org(Guid Id, Guid AssetId);

    /// <summary>A tenant with one domain; free keeps one month, custom keeps everything.</summary>
    async Task<Org> NewOrg(string tier, int? overrideMonths = null)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var t = new Tenant { Name = "Ret " + Guid.NewGuid().ToString("N")[..8], TierKey = tier, OverrideRetentionMonths = overrideMonths };
        db.Tenants.Add(t);
        var asset = new Asset { TenantId = t.Id, Name = $"r.{t.Id:N}.test.com" };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return new Org(t.Id, asset.Id);
    }

    static CheckResult R(Org o, string type, CheckStatus s, int daysAgo) =>
        new() { TenantId = o.Id, AssetId = o.AssetId, CheckType = type, Status = s, Summary = "Sum_DnsOk", ObservedAt = Now.AddDays(-daysAgo) };

    static async Task Add(AppDbContext db, params object[] rows) { db.AddRange(rows); await db.SaveChangesAsync(); }

    async Task<RetentionResult> Run()
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(Now);
    }

    async Task<(int Results, int Changes, int Alerts)> Counts(Guid tenant)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.AcrossTenants<CheckResult>().CountAsync(x => x.TenantId == tenant),
                await db.AcrossTenants<Change>().CountAsync(x => x.TenantId == tenant),
                await db.AcrossTenants<AlertLogEntry>().CountAsync(x => x.TenantId == tenant));
    }

    async Task<List<(string Type, int Days)>> Kept(Guid tenant)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.AcrossTenants<CheckResult>().Where(x => x.TenantId == tenant).Select(x => new { x.CheckType, x.ObservedAt }).ToListAsync();
        return rows.Select(x => (x.CheckType, (int)Math.Round((Now - x.ObservedAt).TotalDays))).OrderBy(x => x.CheckType).ThenBy(x => x.Item2).ToList();
    }

    [DbFact]
    public async Task Results_changes_and_alert_log_older_than_the_cutoff_go_and_newer_ones_stay()
    {
        var org = await NewOrg(Tiers.Free);                                          // one month
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                R(org, "dns", CheckStatus.Ok, 80), R(org, "dns", CheckStatus.Ok, 40), R(org, "dns", CheckStatus.Ok, 10), R(org, "dns", CheckStatus.Ok, 1),
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "old", DetectedAt = Now.AddDays(-45) },
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "new", DetectedAt = Now.AddDays(-5) },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "old", At = Now.AddDays(-45), Success = true },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "new", At = Now.AddDays(-5), Success = true });
        }

        var removed = await Run();

        Assert.Equal([("dns", 1), ("dns", 10)], await Kept(org.Id));
        using var check = Scope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(["new"], await db2.AcrossTenants<Change>().Where(c => c.TenantId == org.Id).Select(c => c.Key).ToListAsync());
        Assert.Equal(["new"], await db2.AcrossTenants<AlertLogEntry>().Where(a => a.TenantId == org.Id).Select(a => a.Subject).ToListAsync());
        Assert.True(removed.Results >= 2 && removed.Changes >= 1 && removed.AlertLog >= 1);
    }

    [DbFact]
    public async Task The_cutoff_is_exactly_retention_months_before_now()
    {
        var org = await NewOrg(Tiers.Free);
        var cutoff = Now.AddMonths(-1);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                new CheckResult { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ObservedAt = cutoff.AddMinutes(-1) },
                new CheckResult { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ObservedAt = cutoff.AddMinutes(1) },
                new CheckResult { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ObservedAt = Now });
        }
        await Run();
        Assert.Equal(2, (await Counts(org.Id)).Results);                              // the one just before the cutoff went
    }

    [DbFact]
    public async Task The_newest_result_of_every_check_type_stays_even_when_it_is_old()
    {
        var org = await NewOrg(Tiers.Free);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                R(org, "dns", CheckStatus.Ok, 200), R(org, "dns", CheckStatus.Ok, 100),         // newest dns is 100 days old
                R(org, "tls", CheckStatus.Ok, 300),                                             // a single, ancient result
                R(org, "ct", CheckStatus.Ok, 90), R(org, "ct", CheckStatus.Ok, 3));
        }
        await Run();
        Assert.Equal([("ct", 3), ("dns", 100), ("tls", 300)], await Kept(org.Id));
    }

    [DbFact]
    public async Task The_newest_good_result_stays_and_older_errors_go_when_a_newer_result_exists()
    {
        var org = await NewOrg(Tiers.Free);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                // dns: the good result at 60 is the baseline for the newer errors, so it stays; the old errors before it go
                R(org, "dns", CheckStatus.Error, 120), R(org, "dns", CheckStatus.Ok, 60), R(org, "dns", CheckStatus.Error, 50), R(org, "dns", CheckStatus.Error, 2),
                // tls: only errors; the newest error stays (newest overall), the older ones go
                R(org, "tls", CheckStatus.Error, 100), R(org, "tls", CheckStatus.Error, 50),
                // ct: an old error followed by a newer good result: the error goes, the good one is the newest and stays
                R(org, "ct", CheckStatus.Error, 70), R(org, "ct", CheckStatus.Warning, 45));
        }
        await Run();
        Assert.Equal([("ct", 45), ("dns", 2), ("dns", 50), ("dns", 60), ("tls", 50)], await Kept(org.Id));
    }

    [DbFact]
    public async Task A_tenant_with_unlimited_retention_loses_nothing()
    {
        var custom = await NewOrg(Tiers.Custom);                                      // tier has no limit
        var overridden = await NewOrg(Tiers.Free, overrideMonths: -1);                // override says unlimited
        foreach (var org in new[] { custom, overridden })
        {
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                R(org, "dns", CheckStatus.Ok, 900), R(org, "dns", CheckStatus.Error, 800), R(org, "dns", CheckStatus.Ok, 700), R(org, "dns", CheckStatus.Ok, 1),
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "k", DetectedAt = Now.AddDays(-900) },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "s", At = Now.AddDays(-900), Success = true });
        }
        await Run();
        Assert.Equal((4, 1, 1), await Counts(custom.Id));
        Assert.Equal((4, 1, 1), await Counts(overridden.Id));
    }

    [DbFact]
    public async Task Tenants_are_isolated_and_a_per_tenant_override_is_respected()
    {
        var shortKeeper = await NewOrg(Tiers.Free);                                   // one month
        var longKeeper = await NewOrg(Tiers.Free, overrideMonths: 6);                 // override: six months
        var other = await NewOrg(Tiers.Custom);                                       // keeps everything
        foreach (var org in new[] { shortKeeper, longKeeper, other })
        {
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                R(org, "dns", CheckStatus.Ok, 300), R(org, "dns", CheckStatus.Ok, 90), R(org, "dns", CheckStatus.Ok, 45), R(org, "dns", CheckStatus.Ok, 2),
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "old", DetectedAt = Now.AddDays(-90) },
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "mid", DetectedAt = Now.AddDays(-45) },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "old", At = Now.AddDays(-90), Success = true },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "mid", At = Now.AddDays(-45), Success = true });
        }

        await Run();

        Assert.Equal([("dns", 2)], await Kept(shortKeeper.Id));
        Assert.Equal((1, 0, 0), await Counts(shortKeeper.Id));
        Assert.Equal([("dns", 2), ("dns", 45), ("dns", 90)], await Kept(longKeeper.Id));   // only the 300-day-old one went
        Assert.Equal((3, 2, 2), await Counts(longKeeper.Id));
        Assert.Equal((4, 2, 2), await Counts(other.Id));                              // untouched by the others' retention
    }

    [DbFact]
    public async Task The_returned_counts_match_the_rows_removed()
    {
        var org = await NewOrg(Tiers.Free);
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Add(db,
                R(org, "dns", CheckStatus.Ok, 90), R(org, "dns", CheckStatus.Ok, 80), R(org, "dns", CheckStatus.Ok, 70), R(org, "dns", CheckStatus.Ok, 1),
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "a", DetectedAt = Now.AddDays(-60) },
                new Change { TenantId = org.Id, AssetId = org.AssetId, CheckType = "dns", Key = "b", DetectedAt = Now.AddDays(-50) },
                new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "x", At = Now.AddDays(-60), Success = true });
        }
        var before = await Counts(org.Id);
        var removed = await Run();
        var after = await Counts(org.Id);

        Assert.Equal((4, 2, 1), before);
        Assert.Equal((1, 0, 0), after);
        // Other tenants in the database may be swept in the same run, so the totals are at least what this tenant lost.
        Assert.True(removed.Results >= 3 && removed.Changes >= 2 && removed.AlertLog >= 1);

        await Run();                                                                  // a second run finds nothing more for this tenant
        Assert.Equal(after, await Counts(org.Id));
    }
}
