namespace FullyQualified.Web;

/// <summary>
/// The web role runs no job runner, so nothing told the monitor it was alive. This beats like a scan worker does (same table, role "web"),
/// so every web replica shows up with its host and version. It never counts as a scanner: the settings test ignores web rows.
/// </summary>
public sealed class WebHeartbeat(IServiceProvider sp, ILogger<WebHeartbeat> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var beat = new WorkerHeartbeat(TimeProvider.System, "/tmp/fq-web-alive", WorkerHeartbeat.Interval, WorkerHeartbeat.RoleName(HostingRole.Web));
        beat.Start(0);                                     // no loops to watch: the process is alive while it can beat
        using var timer = new PeriodicTimer(WorkerHeartbeat.Interval);
        do
        {
            try { await beat.BeatAsync(sp, false, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Web heartbeat failed"); }   // the database may not be there yet; try again next time
        }
        while (await Next(timer, ct));
    }

    static async Task<bool> Next(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
