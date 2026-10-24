using FullyQualified.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FullyQualified.Tests;

public class SchedulerLockTests
{
    static string Connection(PgFactory f) => f.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;

    static async Task Until(Func<bool> condition, int seconds = 15)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
    }

    [DbFact]
    public async Task Only_one_lock_holder_exists_and_the_other_takes_over_when_its_connection_dies()
    {
        using var f = new PgFactory();
        var cs = Connection(f);
        await using var a = new SchedulerLock(cs);
        await using var b = new SchedulerLock(cs);
        Assert.True(await a.TryHoldAsync(default));
        Assert.False(await b.TryHoldAsync(default));
        Assert.True(await a.TryHoldAsync(default)); // holding is stable across ticks

        // Kill the holder's backend, as a crashed process would
        await using var admin = new NpgsqlConnection(cs);
        await admin.OpenAsync();
        await using (var kill = new NpgsqlCommand("select pg_terminate_backend(l.pid) from pg_locks l where l.locktype = 'advisory' and l.granted", admin))
            await kill.ExecuteScalarAsync();

        Assert.True(await b.TryHoldAsync(default));
        Assert.False(await a.TryHoldAsync(default)); // the old holder noticed and stays out
    }

    [DbFact]
    public async Task Disposing_the_holder_frees_the_lock()
    {
        using var f = new PgFactory();
        var cs = Connection(f);
        var a = new SchedulerLock(cs);
        await using var b = new SchedulerLock(cs);
        Assert.True(await a.TryHoldAsync(default));
        await a.DisposeAsync();
        Assert.True(await b.TryHoldAsync(default));
    }

    static JobRunner Runner(PgFactory f)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Connection(f), ["Scanning:Workers"] = "2",
        }).Build();
        return new JobRunner(f.Services, cfg, NullLogger<JobRunner>.Instance);
    }

    [DbFact]
    public async Task Two_job_runners_on_one_database_schedule_one_at_a_time_and_the_survivor_takes_over()
    {
        using var f = new PgFactory();
        using var a = Runner(f);
        using var b = Runner(f);
        await a.StartAsync(default);
        await Until(() => a.IsScheduler);
        await b.StartAsync(default); // b only claims jobs while a holds the lock
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.True(a.IsScheduler);
        Assert.False(b.IsScheduler);

        await a.StopAsync(default);
        Assert.False(a.IsScheduler);
        await Until(() => b.IsScheduler, 10); // takeover within about 10 seconds
        await b.StopAsync(default);
    }
}
