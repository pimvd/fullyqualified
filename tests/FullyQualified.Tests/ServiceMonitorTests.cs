using System.Net;
using System.Net.Http.Json;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullyQualified.Tests;

/// <summary>The monitor against a real database: what replicas and workers it counts, and what it says when something is wrong.</summary>
public class ServiceMonitorTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    record P(string Role, int AgoSeconds, bool Scheduler = false, string Version = "1.0.0");

    async Task Beats(params P[] processes)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        db.WorkerHeartbeats.RemoveRange(await db.WorkerHeartbeats.ToListAsync());
        foreach (var p in processes)
            db.WorkerHeartbeats.Add(new WorkerHeartbeatRow { WorkerId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddHours(-1), BeatAt = DateTimeOffset.UtcNow.AddSeconds(-p.AgoSeconds), IsScheduler = p.Scheduler, Role = p.Role, Host = "host-" + Guid.NewGuid().ToString("N")[..6], Version = p.Version });
        await db.SaveChangesAsync();
    }

    async Task<MonitorSnapshot> Snapshot()
    {
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture = new("en");   // the words are asserted; in the app the request decides
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ServiceMonitor>().SnapshotAsync(default);
    }

    static ServiceRow Svc(MonitorSnapshot s, string key) => s.Services.Single(x => x.Key == key);

    // ---- web replicas ----

    [DbFact]
    public async Task Web_replicas_are_counted_and_a_replica_that_went_quiet_is_a_warning()
    {
        await Beats(new P("web", 5), new P("web", 20));
        var ok = Svc(await Snapshot(), "web");
        Assert.Equal((Finding.Ok, "2"), (ok.Status, ok.Replicas));
        Assert.Contains("Version 1.0.0", ok.Detail);
        Assert.NotNull(ok.LastBeat);

        await Beats(new P("web", 5), new P("web", 20), new P("web", 300));   // seen five minutes ago, gone now
        var warn = Svc(await Snapshot(), "web");
        Assert.Equal((Finding.Warning, "2 / 3"), (warn.Status, warn.Replicas));
        Assert.Contains("stopped reporting", warn.Detail);
    }

    [DbFact]
    public async Task Web_replicas_on_different_versions_are_a_warning_and_a_silent_web_app_fails()
    {
        await Beats(new P("web", 5, Version: "1.0.0"), new P("web", 5, Version: "1.1.0"));
        var mixed = Svc(await Snapshot(), "web");
        Assert.Equal(Finding.Warning, mixed.Status);
        Assert.Contains("1.0.0", mixed.Detail); Assert.Contains("1.1.0", mixed.Detail);

        await Beats(new P("web", 200));                                    // recently seen, but silent for over a minute
        Assert.Equal(Finding.Fail, Svc(await Snapshot(), "web").Status);

        await Beats();                                                   // nothing yet: a fresh start, not a failure
        Assert.Equal(Finding.Info, Svc(await Snapshot(), "web").Status);
    }

    // ---- scan workers ----

    [DbFact]
    public async Task Scan_workers_pass_with_a_scheduler_and_warn_without_one()
    {
        await Beats(new P("worker", 5, Scheduler: true), new P("worker", 10));
        var ok = Svc(await Snapshot(), "worker");
        Assert.Equal((Finding.Ok, "2"), (ok.Status, ok.Replicas));
        Assert.Contains("Scheduling on host-", ok.Detail);

        await Beats(new P("worker", 5), new P("worker", 10));
        var none = Svc(await Snapshot(), "worker");
        Assert.Equal(Finding.Warning, none.Status);
        Assert.Contains("nothing schedules", none.Detail);
    }

    [DbFact]
    public async Task Web_processes_are_never_mistaken_for_scanners_and_the_all_role_counts_as_both()
    {
        await Beats(new P("web", 5), new P("web", 5));
        var snapshot = await Snapshot();
        Assert.Equal(Finding.Fail, Svc(snapshot, "worker").Status);       // only web processes beat: nothing scans
        Assert.Contains("No scan worker is alive", Svc(snapshot, "worker").Detail);

        await Beats(new P("all", 5, Scheduler: true));                      // one container doing everything
        snapshot = await Snapshot();
        Assert.Equal(Finding.Ok, Svc(snapshot, "worker").Status);
        Assert.Equal(Finding.Ok, Svc(snapshot, "web").Status);
    }

    [DbFact]
    public async Task A_web_heartbeat_does_not_make_the_organizations_settings_test_think_scans_run()
    {
        var org = await NewOrg();
        await Beats(new P("web", 5));
        var text = await (await Factory.As(org.Admin).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false })).Content.ReadAsStringAsync();
        Assert.Contains("Scans are not running", text);

        await Beats(new P("web", 5), new P("worker", 5, Scheduler: true));
        text = await (await Factory.As(org.Admin).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Scans are not running", text);
    }

    [DbFact]
    public async Task Rows_from_before_the_role_was_recorded_count_as_scan_workers()
    {
        await Beats(new P("", 5, Scheduler: true));
        Assert.Equal(Finding.Ok, Svc(await Snapshot(), "worker").Status);
    }

    // ---- database, mail, queue ----

    [DbFact]
    public async Task The_database_line_says_version_size_and_connections()
    {
        var db = Svc(await Snapshot(), "database");
        Assert.Equal(Finding.Ok, db.Status);
        Assert.Matches(@"PostgreSQL \d+.* · .*(MB|GB) · \d+ connections · \d+ ms", db.Detail);
    }

    [DbFact]
    public async Task Without_an_smtp_host_the_mail_line_is_informational()
    {
        var mail = Svc(await Snapshot(), "mail");
        Assert.Equal(Finding.Info, mail.Status);
        Assert.Contains("No SMTP server", mail.Detail);
    }

    [DbFact]
    public async Task A_check_that_has_waited_too_long_makes_the_queue_a_warning()
    {
        var org = await NewOrg();
        var asset = await AddAsset(org.Id, $"q.{org.Id:N}.test.com");
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.RemoveRange(await db.AcrossTenants<Job>().ToListAsync());
            await db.SaveChangesAsync();
            db.Jobs.Add(new Job { TenantId = org.Id, AssetId = asset.Id, CheckType = "dns", RunAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }
        var fine = Svc(await Snapshot(), "queue");
        Assert.Equal(Finding.Ok, fine.Status);
        Assert.Contains("1 waiting", fine.Detail);

        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.AcrossTenants<Job>().SingleAsync(j => j.TenantId == org.Id)).RunAt = DateTimeOffset.UtcNow.AddMinutes(-20);
            await db.SaveChangesAsync();
        }
        var late = Svc(await Snapshot(), "queue");
        Assert.Equal(Finding.Warning, late.Status);
        Assert.Contains("waited 20 min", late.Detail);
    }

    // ---- the page and the heartbeat itself ----

    [DbFact]
    public async Task The_monitor_page_is_for_the_platform_owner_only()
    {
        var org = await NewOrg();
        await Beats(new P("web", 5), new P("worker", 5, Scheduler: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Admin).GetAsync("/monitor")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).GetAsync("/monitor")).StatusCode);

        var page = await (await Factory.As(Owner).GetAsync("/monitor")).Content.ReadAsStringAsync();
        Assert.All(new[] { "Web app", "Scan workers", "Database", "Mail relay", "Scan queue" }, name => Assert.Contains(name, page));
        Assert.Contains("data-process=", page);
    }

    [DbFact]
    public async Task A_beat_records_what_the_process_is_and_where_it_runs()
    {
        await EnsureOwner();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var hb = new WorkerHeartbeat(TimeProvider.System, path, TimeSpan.FromSeconds(60), "web");
        hb.Start(0);
        Assert.True(await hb.BeatAsync(Factory.Services, false, default));
        using var scope = Scope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkerHeartbeats.SingleAsync(r => r.WorkerId == hb.WorkerId);
        Assert.Equal(("web", Environment.MachineName), (row.Role, row.Host));
        Assert.NotEmpty(row.Version);
        File.Delete(path);
    }
}

/// <summary>The mail line against a relay that is down.</summary>
public class ServiceMonitorMailTests(PgProbeFactory factory) : OrgTestBase<PgProbeFactory>(factory), IClassFixture<PgProbeFactory>
{
    [DbFact]
    public async Task A_mail_relay_that_does_not_answer_is_a_failure_that_names_it()
    {
        await EnsureOwner();
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture = new("en");
        using var scope = Scope();
        var mail = (await scope.ServiceProvider.GetRequiredService<ServiceMonitor>().SnapshotAsync(default)).Services.Single(s => s.Key == "mail");
        Assert.Equal(Finding.Fail, mail.Status);
        Assert.Contains("smtp.invalid.test:587", mail.Detail);
        Assert.Contains("connection refused", mail.Detail);
    }
}
