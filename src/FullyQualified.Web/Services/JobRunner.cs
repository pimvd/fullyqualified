namespace FullyQualified.Web;

public class JobRunner(IServiceProvider sp, IConfiguration cfg, ILogger<JobRunner> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.GetValue("Scanning:Enabled", true)) return Task.CompletedTask;
        var workers = cfg.GetValue("Scanning:Workers", 4);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(() => Loop(i == 0, ct), ct)));
    }

    async Task Loop(bool scheduler, CancellationToken ct)
    {
        var nextSchedule = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantScope>().All = true; // background work spans all tenants
                if (scheduler && DateTimeOffset.UtcNow >= nextSchedule)
                {
                    await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueDueAsync(ct);
                    nextSchedule = DateTimeOffset.UtcNow.AddMinutes(1);
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
