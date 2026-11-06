using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullyQualified.Tests;

/// <summary>A check job may not run forever (issue #29): the cap turns it into an Error result and the loop carries on.</summary>
public class CheckRunnerTimeoutTests
{
    sealed class HangingCheck : ICheck
    {
        public string Type => "hang";
        public bool AppliesTo(Asset asset) => true;
        public async Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new(CheckStatus.Ok, "never", []);
        }
    }

    sealed class QuickCheck : ICheck
    {
        public string Type => "quick";
        public bool AppliesTo(Asset asset) => true;
        public Task<CheckOutcome> RunAsync(Asset asset, CancellationToken ct) => Task.FromResult(new CheckOutcome(CheckStatus.Ok, "Sum_CtOk|0|0", []));
    }

    static CheckRunner Runner(IServiceScope scope, string cap)
    {
        var sp = scope.ServiceProvider;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanning:JobTimeoutSeconds"] = cap }).Build();
        return new CheckRunner(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<JobQueue>(), [new HangingCheck(), new QuickCheck()],
            sp.GetRequiredService<AlertService>(), sp.GetRequiredService<PlanService>(), cfg, new KeyLocalizer(), NullLogger<CheckRunner>.Instance);
    }

    static async Task<(Guid Tenant, Guid Asset)> Seed(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var tenant = new Tenant { Name = "Cap " + Guid.NewGuid().ToString("N")[..8], TierKey = Tiers.Free };
        db.Tenants.Add(tenant);
        var asset = new Asset { TenantId = tenant.Id, Name = $"{tenant.Id:N}.test.com", Kind = AssetKind.Domain };
        db.Assets.Add(asset);
        var now = DateTimeOffset.UtcNow;
        db.Jobs.Add(new Job { TenantId = tenant.Id, AssetId = asset.Id, CheckType = "hang", RunAt = now.AddMinutes(-2) });
        db.Jobs.Add(new Job { TenantId = tenant.Id, AssetId = asset.Id, CheckType = "quick", RunAt = now.AddMinutes(-1) });
        await db.SaveChangesAsync();
        return (tenant.Id, asset.Id);
    }

    static IServiceScope Scope(PgFactory f) { var s = f.Services.CreateScope(); s.ServiceProvider.GetRequiredService<TenantScope>().All = true; return s; }

    [DbFact]
    public async Task A_check_that_never_returns_is_stored_as_a_timeout_error_its_job_is_removed_and_the_next_job_runs()
    {
        using var f = new PgFactory();
        using var scope = Scope(f);
        var (_, assetId) = await Seed(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runner = Runner(scope, "1");

        Assert.True(await runner.RunNextAsync(default)); // the hanging job, cut off after 1 s
        var hung = await db.Results.SingleAsync(r => r.AssetId == assetId && r.CheckType == "hang");
        Assert.Equal(CheckStatus.Error, hung.Status);
        Assert.Equal("Sum_Error|Check timed out after 1 s", hung.Summary);
        Assert.False(await db.Jobs.AnyAsync(j => j.AssetId == assetId && j.CheckType == "hang"));
        Assert.Empty(await db.Changes.Where(c => c.AssetId == assetId).ToListAsync());

        Assert.True(await runner.RunNextAsync(default)); // the loop carries on with the next one
        Assert.Equal(CheckStatus.Ok, (await db.Results.SingleAsync(r => r.AssetId == assetId && r.CheckType == "quick")).Status);
        Assert.False(await db.Jobs.AnyAsync(j => j.AssetId == assetId));
    }

    [DbFact]
    public async Task Cancelling_the_host_token_during_a_check_stops_the_loop_and_stores_nothing()
    {
        using var f = new PgFactory();
        using var scope = Scope(f);
        var (_, assetId) = await Seed(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runner = Runner(scope, "90");

        using var host = new CancellationTokenSource();
        var run = runner.RunNextAsync(host.Token);
        await Task.Delay(500); // the hanging job has been claimed and is running
        host.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(await db.Results.AnyAsync(r => r.AssetId == assetId));
    }
}
