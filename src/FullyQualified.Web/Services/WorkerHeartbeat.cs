using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>
/// The liveness signal of a process that runs a <see cref="JobRunner"/> (ADR 0010). It beats only while every loop has finished a tick
/// recently, so a stuck loop stops the beat. A beat upserts a <c>worker_heartbeats</c> row and touches a file the compose healthcheck reads.
/// </summary>
public sealed class WorkerHeartbeat(TimeProvider clock, string filePath)
{
    public const string DefaultFile = "/tmp/fq-worker-alive";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxTickAge = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RowLifetime = TimeSpan.FromDays(1);

    public Guid WorkerId { get; } = Guid.NewGuid();
    readonly DateTimeOffset startedAt = clock.GetUtcNow();
    long[] ticks = [];

    /// <summary>Starts watching this many loops; each counts as having ticked now.</summary>
    public void Start(int loops)
    {
        var now = clock.GetUtcNow().UtcTicks;
        ticks = Enumerable.Repeat(now, loops).ToArray();
    }

    /// <summary>A loop calls this every time it begins a pass.</summary>
    public void Tick(int loop) => Interlocked.Exchange(ref ticks[loop], clock.GetUtcNow().UtcTicks);

    public bool Healthy
    {
        get
        {
            var limit = (clock.GetUtcNow() - MaxTickAge).UtcTicks;
            return ticks.All(t => Interlocked.Read(ref t) >= limit);
        }
    }

    /// <summary>Beats once if every loop is alive; returns whether it did. A database failure is the caller's to log, the file is touched first.</summary>
    public async Task<bool> BeatAsync(IServiceProvider sp, bool isScheduler, CancellationToken ct)
    {
        if (!Healthy) return false;
        File.WriteAllBytes(filePath, []);
        File.SetLastWriteTimeUtc(filePath, clock.GetUtcNow().UtcDateTime);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            insert into worker_heartbeats ("WorkerId", "StartedAt", "BeatAt", "IsScheduler") values ({WorkerId}, {startedAt}, {now}, {isScheduler})
            on conflict ("WorkerId") do update set "BeatAt" = excluded."BeatAt", "IsScheduler" = excluded."IsScheduler"
            """, ct);
        return true;
    }

    /// <summary>Removes rows of processes that stopped beating long ago. The scheduler calls this.</summary>
    public async Task PruneAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - RowLifetime;
        await db.WorkerHeartbeats.Where(x => x.BeatAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
