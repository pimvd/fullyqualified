using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>
/// The liveness signal of a process that runs a <see cref="JobRunner"/> (ADR 0010). It beats only while every loop has finished a tick
/// recently, so a stuck loop stops the beat. A beat upserts a <c>worker_heartbeats</c> row and touches a file the compose healthcheck reads.
/// </summary>
public sealed class WorkerHeartbeat(TimeProvider clock, string filePath, TimeSpan maxTickAge, string role = "worker")
{
    public const string DefaultFile = "/tmp/fq-worker-alive";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    /// <summary>How long a job may run (<c>Scanning:JobTimeoutSeconds</c>, default 90).</summary>
    public static TimeSpan JobTimeout(IConfiguration cfg) => TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Scanning:JobTimeoutSeconds", 90)));
    /// <summary>A loop that has not started a pass for the job cap plus this long is stuck.</summary>
    public static readonly TimeSpan TickSlack = TimeSpan.FromSeconds(60);
    /// <summary>The window in which a loop must have started a pass: the job cap plus <see cref="TickSlack"/> (150 s by default).</summary>
    public static TimeSpan WindowFor(IConfiguration cfg) => JobTimeout(cfg) + TickSlack;
    /// <summary>A process that has been silent for this long is gone for good, and its row is removed.</summary>
    public static readonly TimeSpan RowLifetime = TimeSpan.FromHours(1);
    /// <summary>Every process cleans up on its first beat (a replaced container removes its predecessor's row) and then every this many beats.</summary>
    const int PruneEveryBeats = 20;

    public TimeSpan MaxTickAge { get; } = maxTickAge;

    /// <summary>What this process is, as it is recorded: "web", "worker" or "all".</summary>
    public static string RoleName(HostingRole role) => role.ToString().ToLowerInvariant();
    static readonly string Version = typeof(WorkerHeartbeat).Assembly.GetName().Version?.ToString(3) ?? "";

    public Guid WorkerId { get; } = Guid.NewGuid();
    readonly DateTimeOffset startedAt = clock.GetUtcNow();
    long[] ticks = [];
    int beats;

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
            insert into worker_heartbeats ("WorkerId", "StartedAt", "BeatAt", "IsScheduler", "Role", "Host", "Version")
            values ({WorkerId}, {startedAt}, {now}, {isScheduler}, {role}, {Environment.MachineName}, {Version})
            on conflict ("WorkerId") do update set "BeatAt" = excluded."BeatAt", "IsScheduler" = excluded."IsScheduler"
            """, ct);
        if (beats++ % PruneEveryBeats == 0) await PruneAsync(db, ct);
        return true;
    }

    /// <summary>Removes rows of processes that stopped beating long ago. Every beating process does this now and then, and the scheduler too.</summary>
    public async Task PruneAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - RowLifetime;
        await db.WorkerHeartbeats.Where(x => x.BeatAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
