using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullyQualified.Tests;

public class WorkerHeartbeatTests
{
    sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_loop_that_has_not_ticked_for_over_a_minute_makes_the_process_unhealthy()
    {
        var clock = new FakeClock(T0);
        var hb = new WorkerHeartbeat(clock, Path.GetTempFileName());
        hb.Start(2);
        Assert.True(hb.Healthy);

        clock.Now = T0.AddSeconds(50);
        hb.Tick(0); // loop 1 is stuck
        Assert.True(hb.Healthy);

        clock.Now = T0.AddSeconds(61);
        hb.Tick(0);
        Assert.False(hb.Healthy);

        hb.Tick(1); // it recovers
        Assert.True(hb.Healthy);
    }

    [Fact]
    public async Task A_stuck_loop_stops_the_beat_and_leaves_the_file_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var clock = new FakeClock(T0);
        var hb = new WorkerHeartbeat(clock, path);
        hb.Start(1);
        clock.Now = T0.AddSeconds(61);
        Assert.False(await hb.BeatAsync(null!, false, default)); // returns before touching the file or the database
        Assert.False(File.Exists(path));
    }

    [DbFact]
    public async Task A_beat_upserts_one_row_and_touches_the_file_and_the_scheduler_prunes_old_rows()
    {
        using var f = new PgFactory();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var hb = new WorkerHeartbeat(clock, path);
        hb.Start(1);
        Assert.True(await hb.BeatAsync(f.Services, false, default));
        clock.Now = clock.Now.AddSeconds(15);
        hb.Tick(0);
        Assert.True(await hb.BeatAsync(f.Services, true, default));
        Assert.True(File.Exists(path));
        File.Delete(path);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkerHeartbeats.SingleAsync(x => x.WorkerId == hb.WorkerId);
        Assert.True(row.IsScheduler);
        Assert.Equal(clock.Now, row.BeatAt, TimeSpan.FromMilliseconds(1));
        Assert.True(row.BeatAt > row.StartedAt);

        db.WorkerHeartbeats.Add(new WorkerHeartbeatRow { WorkerId = Guid.NewGuid(), StartedAt = clock.Now.AddDays(-3), BeatAt = clock.Now.AddDays(-2) });
        await db.SaveChangesAsync();
        await hb.PruneAsync(db, default);
        Assert.Equal(hb.WorkerId, (await db.WorkerHeartbeats.SingleAsync()).WorkerId);
    }

    [DbFact]
    public async Task A_running_job_runner_has_a_fresh_row_that_follows_the_scheduler_lock()
    {
        using var f = new PgFactory();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = f.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default"), ["Scanning:Workers"] = "2",
        }).Build();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var hb = new WorkerHeartbeat(TimeProvider.System, path);
        using var runner = new JobRunner(f.Services, cfg, NullLogger<JobRunner>.Instance, hb);
        await runner.StartAsync(default);
        try
        {
            // the first beat can come before the lock is taken, so the scheduler flag shows from the second beat on
            for (var i = 0; i < 300 && !(File.Exists(path) && await Row(f, hb) is { IsScheduler: true }); i++) await Task.Delay(100);
            Assert.True(File.Exists(path));
            var row = await Row(f, hb);
            Assert.NotNull(row);
            Assert.True(row.IsScheduler);
            Assert.True(DateTimeOffset.UtcNow - row.BeatAt < TimeSpan.FromSeconds(60));
        }
        finally { await runner.StopAsync(default); File.Delete(path); }
    }

    static async Task<WorkerHeartbeatRow?> Row(PgFactory f, WorkerHeartbeat hb)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkerHeartbeats.AsNoTracking().SingleOrDefaultAsync(x => x.WorkerId == hb.WorkerId);
    }
}
