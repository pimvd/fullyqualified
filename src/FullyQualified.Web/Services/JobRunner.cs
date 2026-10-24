namespace FullyQualified.Web;

public class JobRunner(IServiceProvider sp, IConfiguration cfg, ILogger<JobRunner> log, WorkerHeartbeat? heartbeat = null) : BackgroundService
{
    readonly WorkerHeartbeat beat = heartbeat ?? new WorkerHeartbeat(TimeProvider.System, WorkerHeartbeat.DefaultFile);
    volatile bool isScheduler;
    /// <summary>True while this process holds the scheduler lock.</summary>
    public bool IsScheduler => isScheduler;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.GetValue("Scanning:Enabled", true)) return Task.CompletedTask;
        var workers = cfg.GetValue("Scanning:Workers", 4);
        beat.Start(workers);
        var loops = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            try { await Loop(i, ct); }
            finally { if (i == 0) isScheduler = false; } // the lock goes with the loop
        }, ct));
        return Task.WhenAll(loops.Append(Task.Run(() => BeatLoop(ct), ct)));
    }

    async Task BeatLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WorkerHeartbeat.Interval, TimeProvider.System);
        do
        {
            try { await beat.BeatAsync(sp, isScheduler, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Heartbeat failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    async Task<bool> HoldAsync(SchedulerLock schedulerLock, CancellationToken ct)
    {
        try { return isScheduler = await schedulerLock.TryHoldAsync(ct); }
        catch { isScheduler = false; throw; }
    }

    async Task Loop(int index, CancellationToken ct)
    {
        var scheduler = index == 0;
        var nextSchedule = DateTimeOffset.MinValue;
        var nextRetention = DateTimeOffset.MinValue;
        await using var schedulerLock = scheduler ? new SchedulerLock(Hosting.ConnectionString(cfg)) : null;
        while (!ct.IsCancellationRequested)
        {
            beat.Tick(index);
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantScope>().All = true; // background work spans all tenants
                // Only the process holding the advisory lock schedules; the others just claim jobs and retry the lock each tick.
                var holds = schedulerLock is not null && await HoldAsync(schedulerLock, ct);
                if (!holds) nextSchedule = nextRetention = DateTimeOffset.MinValue; // a new holder starts with a full pass, retention included
                if (holds && DateTimeOffset.UtcNow >= nextSchedule)
                {
                    await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(ct);
                    nextSchedule = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                if (holds && DateTimeOffset.UtcNow >= nextRetention)
                {
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(DateTimeOffset.UtcNow, ct);
                    await beat.PruneAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), ct);
                    nextRetention = DateTimeOffset.UtcNow.AddHours(6);
                }
                if (await scope.ServiceProvider.GetRequiredService<CheckRunner>().RunNextAsync(ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Job loop error"); }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
