namespace FullyQualified.Web;

public class JobRunner(IServiceProvider sp, IConfiguration cfg, ILogger<JobRunner> log) : BackgroundService
{
    volatile bool isScheduler;
    /// <summary>True while this process holds the scheduler lock.</summary>
    public bool IsScheduler => isScheduler;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.GetValue("Scanning:Enabled", true)) return Task.CompletedTask;
        var workers = cfg.GetValue("Scanning:Workers", 4);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            try { await Loop(i == 0, ct); }
            finally { if (i == 0) isScheduler = false; } // the lock goes with the loop
        }, ct)));
    }

    async Task<bool> HoldAsync(SchedulerLock schedulerLock, CancellationToken ct)
    {
        try { return isScheduler = await schedulerLock.TryHoldAsync(ct); }
        catch { isScheduler = false; throw; }
    }

    async Task Loop(bool scheduler, CancellationToken ct)
    {
        var nextSchedule = DateTimeOffset.MinValue;
        var nextRetention = DateTimeOffset.MinValue;
        await using var schedulerLock = scheduler ? new SchedulerLock(Hosting.ConnectionString(cfg)) : null;
        while (!ct.IsCancellationRequested)
        {
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
