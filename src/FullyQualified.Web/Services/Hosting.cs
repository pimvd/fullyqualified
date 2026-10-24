using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>What this process does (ADR 0010). One image, one Program.cs: the role decides which services and endpoints exist.</summary>
public enum HostingRole { All, Web, Worker }

public static class Hosting
{
    public static HostingRole RoleOf(IConfiguration cfg) => cfg["Hosting:Role"] switch
    {
        null or "" => HostingRole.All,
        var v when v.Equals("all", StringComparison.OrdinalIgnoreCase) => HostingRole.All,
        var v when v.Equals("web", StringComparison.OrdinalIgnoreCase) => HostingRole.Web,
        var v when v.Equals("worker", StringComparison.OrdinalIgnoreCase) => HostingRole.Worker,
        var v => throw new InvalidOperationException($"Hosting:Role must be all, web or worker, not '{v}'."),
    };

    /// <summary>The worker has no UI to serve, so it is a plain generic host: no Kestrel, no port, no endpoints.</summary>
    public static HostApplicationBuilder CreateWorkerBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddFullyQualified(builder.Configuration, HostingRole.Worker);
        return builder;
    }

    /// <summary>The same container for every role: the services scans, alarms and the web UI share, plus the job runner where the role has one.</summary>
    public static IServiceCollection AddFullyQualified(this IServiceCollection services, IConfiguration cfg, HostingRole role)
    {
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")
            ?? "Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres"));
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.AddHttpClient();
        services.AddHttpClient("ct", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("FullyQualified/0.1 (+https://github.com/pimvd/fullyqualified)");
        });

        services.AddSingleton<HostGuard>();
        services.AddSingleton<DigService>();
        services.AddSingleton<ICheck, TlsCheck>();
        services.AddSingleton<ICheck, DnsCheck>();
        services.AddSingleton<ICheck, CtLogCheck>();
        services.AddScoped<JobQueue>();
        services.AddScoped<CheckRunner>();
        services.AddScoped<AlertService>();
        services.AddScoped<ReportBuilder>();
        services.AddScoped<PasskeyService>();
        services.AddScoped<AdminService>();
        services.AddScoped<Session>();
        services.AddScoped<TenantScope>();
        services.AddScoped<DbGate>();
        services.AddScoped<TenantStats>();
        services.AddScoped<PlanService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<PlanAdminService>();
        services.AddScoped<SettingsTestService>();
        services.AddSingleton<ServerProbes>();
        services.AddSingleton<ScanThrottle>();
        services.AddScoped<PublicScanService>();
        services.AddScoped<LogQueries>();
        services.AddScoped<AlertSettingsService>();
        services.AddScoped<SsoService>();
        services.AddMemoryCache();
        // Keys live in the database so cookies survive restarts and redeploys, and so the worker can read the secrets the web side stored.
        services.AddDataProtection().SetApplicationName("FullyQualified").PersistKeysToDbContext<AppDbContext>();

        if (role != HostingRole.Worker)
        {
            services.AddRazorComponents();
            services.AddToolbox();
            services.AddEmailTests();
        }
        if (role != HostingRole.Web) services.AddHostedService<JobRunner>();
        return services;
    }
}
