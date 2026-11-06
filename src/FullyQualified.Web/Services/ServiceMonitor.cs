using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

/// <summary>One process that beats: a web replica or a scan worker.</summary>
public record ProcessRow(Guid Id, string Role, string Host, string Version, DateTimeOffset StartedAt, DateTimeOffset BeatAt, bool IsScheduler, bool Alive);

/// <summary>One line of the monitor: a service, how it is doing and the facts behind that, already in the reader's language.</summary>
public record ServiceRow(string Key, Finding Status, string Replicas, DateTimeOffset? LastBeat, string Detail);

/// <param name="HiddenProcesses">Processes that have been silent for longer than <see cref="ServiceMonitor.RecentlySeen"/>: not listed, but counted.</param>
public record MonitorSnapshot(DateTimeOffset At, List<ServiceRow> Services, List<ProcessRow> Processes, int HiddenProcesses = 0);

/// <summary>
/// What the platform owner sees on the monitor page: the web replicas and the scan workers (from their heartbeats), the database, the mail relay
/// and the scan queue. Nothing here changes anything; it only looks.
/// </summary>
public class ServiceMonitor(AppDbContext db, ServerProbes probes, IConfiguration cfg, IStringLocalizer<SharedResource> L)
{
    /// <summary>A web process beats every 15 seconds, so one that has been quiet for four beats is gone.</summary>
    public static readonly TimeSpan WebWindow = TimeSpan.FromSeconds(60);
    /// <summary>A process that stopped beating this recently still counts as a replica that should be there ("1 of 2").</summary>
    public static readonly TimeSpan RecentlySeen = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StuckQueue = TimeSpan.FromMinutes(15);
    public const int SlowDatabaseMs = 500;

    /// <summary>Rows from before the role was recorded came from job runners.</summary>
    static string RoleOf(WorkerHeartbeatRow r) => string.IsNullOrEmpty(r.Role) ? "worker" : r.Role;

    public async Task<MonitorSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkerHeartbeats.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync(ct);
        var scanWindow = WorkerHeartbeat.WindowFor(cfg);
        bool Alive(WorkerHeartbeatRow r) => now - r.BeatAt <= (RoleOf(r) == "web" ? WebWindow : scanWindow);

        // Old silent processes (a container that was replaced, a replica scaled away) are not worth a row; they are counted and removed after an hour.
        var recent = rows.Where(r => now - r.BeatAt <= RecentlySeen).ToList();
        var processes = recent.Select(r => new ProcessRow(r.WorkerId, RoleOf(r), r.Host, r.Version, r.StartedAt, r.BeatAt, r.IsScheduler, Alive(r)))
            .OrderBy(p => p.Role).ThenBy(p => p.StartedAt).ToList();

        return new MonitorSnapshot(now,
        [
            Web(recent, Alive),
            Workers(recent, Alive),
            await DatabaseAsync(ct),
            await MailAsync(ct),
            await QueueAsync(now, ct),
        ], processes, rows.Count - recent.Count);
    }

    ServiceRow Web(List<WorkerHeartbeatRow> recent, Func<WorkerHeartbeatRow, bool> alive)
    {
        // A process that runs everything in one container is a web replica too.
        var members = recent.Where(r => RoleOf(r) is "web" or "all").ToList();
        if (members.Count == 0) return new("web", Finding.Info, "0", null, L["Monitor_WebWaiting"].Value);
        var up = members.Where(alive).ToList();
        var versions = up.Select(r => r.Version).Where(v => v.Length > 0).Distinct().ToList();
        var status = up.Count == 0 ? Finding.Fail : up.Count < members.Count || versions.Count > 1 ? Finding.Warning : Finding.Ok;
        var detail = up.Count == 0 ? L["Monitor_WebDown"].Value
            : up.Count < members.Count ? L["Monitor_ReplicaMissing", members.Count - up.Count].Value
            : versions.Count > 1 ? L["Monitor_MixedVersions", string.Join(", ", versions)].Value
            : L["Monitor_Version", versions.FirstOrDefault() ?? "—"].Value;
        return new("web", status, Replicas(up.Count, members.Count), members.Max(r => r.BeatAt), detail);
    }

    ServiceRow Workers(List<WorkerHeartbeatRow> recent, Func<WorkerHeartbeatRow, bool> alive)
    {
        var members = recent.Where(r => RoleOf(r) is "worker" or "all").ToList();
        var up = members.Where(alive).ToList();
        var scheduler = up.FirstOrDefault(r => r.IsScheduler);
        if (up.Count == 0) return new("worker", Finding.Fail, Replicas(0, members.Count), members.Count == 0 ? null : members.Max(r => r.BeatAt), L["Monitor_NoScanner"].Value);
        var status = scheduler is null || up.Count < members.Count ? Finding.Warning : Finding.Ok;
        var detail = scheduler is null ? L["Monitor_NoScheduler"].Value
            : up.Count < members.Count ? L["Monitor_ReplicaMissing", members.Count - up.Count].Value
            : L["Monitor_SchedulerOn", scheduler.Host.Length > 0 ? scheduler.Host : scheduler.WorkerId.ToString("N")[..8]].Value;
        return new("worker", status, Replicas(up.Count, members.Count), up.Max(r => r.BeatAt), detail);
    }

    static string Replicas(int up, int total) => up == total ? up.ToString(CultureInfo.InvariantCulture) : $"{up} / {total}";

    async Task<ServiceRow> DatabaseAsync(CancellationToken ct)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            await db.Database.OpenConnectionAsync(ct);
            string version; long bytes; long connections;
            try
            {
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "select current_setting('server_version'), pg_database_size(current_database()), (select count(*) from pg_stat_activity where datname = current_database())";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                (version, bytes, connections) = (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
            }
            finally { await db.Database.CloseConnectionAsync(); }
            watch.Stop();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            var detail = L["Monitor_DbDetail", version, Bytes(bytes), connections, watch.ElapsedMilliseconds].Value;
            if (pending > 0) return new("database", Finding.Fail, "—", null, L["Monitor_DbPending", pending].Value + " " + detail);
            return new("database", watch.ElapsedMilliseconds > SlowDatabaseMs ? Finding.Warning : Finding.Ok, "—", null, detail);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new("database", Finding.Fail, "—", null, L["Monitor_DbDown", ex.Message.Length > 200 ? ex.Message[..200] : ex.Message].Value);
        }
    }

    async Task<ServiceRow> MailAsync(CancellationToken ct)
    {
        if (cfg["Alerts:Email:Host"] is not { Length: > 0 } host) return new("mail", Finding.Info, "—", null, L["Monitor_MailNone"].Value);
        var port = cfg.GetValue("Alerts:Email:Port", 587);
        var watch = Stopwatch.StartNew();
        var error = await probes.TcpAsync(host, port, ct);
        watch.Stop();
        return error is null
            ? new("mail", Finding.Ok, "—", null, L["Monitor_MailUp", $"{host}:{port}", watch.ElapsedMilliseconds].Value)
            : new("mail", Finding.Fail, "—", null, L["Monitor_MailDown", $"{host}:{port}", error].Value);
    }

    async Task<ServiceRow> QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var jobs = db.AcrossTenants<Job>();
        var waiting = await jobs.CountAsync(j => j.State == "pending", ct);
        var running = await jobs.CountAsync(j => j.State == "running", ct);
        var oldest = await jobs.Where(j => j.State == "pending" && j.RunAt <= now).OrderBy(j => j.RunAt).Select(j => (DateTimeOffset?)j.RunAt).FirstOrDefaultAsync(ct);
        var late = oldest is { } o ? now - o : TimeSpan.Zero;
        var detail = L["Monitor_QueueDetail", waiting, running].Value + (oldest is null ? "" : " " + L["Monitor_QueueOldest", (int)Math.Max(0, late.TotalMinutes)].Value);
        return new("queue", late > StuckQueue ? Finding.Warning : Finding.Ok, "—", null, detail);
    }

    static string Bytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : $"{b / (double)(1L << 20):0.#} MB";
}
