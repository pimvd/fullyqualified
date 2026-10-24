using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FullyQualified.Tests;

public class HostingRoleFactory(string? role) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:AutoMigrate", "false");
        builder.UseSetting("Scanning:Enabled", "false");
        if (role is not null) builder.UseSetting("Hosting:Role", role);
    }
}

public class HostingRoleTests
{
    static IConfiguration Config(string? role) => new ConfigurationBuilder()
        .AddInMemoryCollection(role is null ? [] : new Dictionary<string, string?> { ["Hosting:Role"] = role }).Build();

    static bool HasJobRunner(IServiceCollection s) => s.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner));

    [Theory]
    [InlineData(null, HostingRole.All)]
    [InlineData("", HostingRole.All)]
    [InlineData("all", HostingRole.All)]
    [InlineData("WEB", HostingRole.Web)]
    [InlineData("worker", HostingRole.Worker)]
    public void The_role_defaults_to_all_and_ignores_case(string? value, HostingRole expected) => Assert.Equal(expected, Hosting.RoleOf(Config(value)));

    [Fact]
    public void An_unknown_role_fails_at_start() => Assert.Throws<InvalidOperationException>(() => Hosting.RoleOf(Config("scanner")));

    [Theory]
    [InlineData(HostingRole.All, true)]
    [InlineData(HostingRole.Web, false)]
    [InlineData(HostingRole.Worker, true)]
    public void Only_roles_that_scan_register_the_job_runner(HostingRole role, bool expected)
        => Assert.Equal(expected, HasJobRunner(new ServiceCollection().AddFullyQualified(Config(null), role)));

    [Fact]
    public void The_worker_registers_no_web_ui_services()
    {
        var worker = new ServiceCollection().AddFullyQualified(Config(null), HostingRole.Worker);
        Assert.DoesNotContain(worker, d => d.ServiceType == typeof(ToolNetwork));
        Assert.Contains(new ServiceCollection().AddFullyQualified(Config(null), HostingRole.Web), d => d.ServiceType == typeof(ToolNetwork));
        Assert.Contains(worker, d => d.ServiceType == typeof(CheckRunner)); // the services a scan needs are there in every role
    }

    [Fact]
    public void The_all_role_runs_the_job_runner_in_the_web_host_and_the_web_role_does_not()
    {
        using var all = new HostingRoleFactory(null);
        Assert.Contains(all.Services.GetServices<IHostedService>(), h => h is JobRunner);
        using var web = new HostingRoleFactory("web");
        Assert.DoesNotContain(web.Services.GetServices<IHostedService>(), h => h is JobRunner);
    }

    [Fact]
    public async Task The_web_role_still_serves_requests()
    {
        using var web = new HostingRoleFactory("web");
        Assert.True((await web.CreateClient().GetAsync("/health")).IsSuccessStatusCode);
    }

    [Fact]
    public void The_worker_host_has_only_the_job_runner_and_no_web_server()
    {
        var builder = Hosting.CreateWorkerBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Hosting:Role"] = "worker" });
        using var host = builder.Build();
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is JobRunner);
        Assert.DoesNotContain(hosted, h => h.GetType().Name.Contains("GenericWebHost")); // no Kestrel, so no port
        Assert.Null(host.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>());
    }
}
