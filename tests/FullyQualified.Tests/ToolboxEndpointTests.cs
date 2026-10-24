using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class ToolboxEndpointTests(RoleFactory factory) : IClassFixture<RoleFactory>
{
    [Fact]
    public async Task Anonymous_execution_is_rejected_without_a_login_redirect()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/toolbox", new { tool = "headers", text = "From: a@example.com" })).StatusCode);
    }

    [Fact]
    public async Task Execution_requires_an_antiforgery_token_even_for_signed_in_users()
    {
        var response = await factory.As("Viewer").PostAsJsonAsync("/api/toolbox", new { tool = "headers", text = "From: a@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Member")]
    [InlineData("Admin")]
    public async Task Every_role_can_analyze_headers_without_creating_a_monitoring_asset(string role)
    {
        var client = factory.As(role);
        await Token(client);
        var response = await client.PostAsJsonAsync("/api/toolbox", new { tool = "headers", text = "From: Alice <alice@example.org>\r\nAuthentication-Results: mx.example; dmarc=pass header.from=example.org" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("example.org", json);
        Assert.DoesNotContain("Tool_", json);
    }

    [Fact]
    public async Task Invalid_tools_and_oversized_inputs_are_rejected_without_echoing_them()
    {
        var client = factory.As("Viewer");
        await Token(client);
        var invalid = await client.PostAsJsonAsync("/api/toolbox", new { tool = "secret-unknown-tool" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("secret-unknown-tool", await invalid.Content.ReadAsStringAsync());
        var big = await client.PostAsJsonAsync("/api/toolbox", new { tool = "headers", text = new string('a', 65537) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }

    internal static async Task Token(HttpClient client)
    {
        var page = await client.GetStringAsync("/toolbox");
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }
}

public class ToolboxPrivacyTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    [DbFact]
    public async Task One_time_tools_do_not_write_monitoring_rows_or_leak_between_tenants()
    {
        var first = await NewOrg();
        var other = await NewOrg();
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<FullyQualified.Web.AppDbContext>();
        var before = await Counts(db);
        var client = Factory.As(first.Viewer);
        await ToolboxEndpointTests.Token(client);
        var response = await client.PostAsJsonAsync("/api/toolbox", new { tool = "headers", text = "From: confidential@private.example\r\nReceived: by internal.private.example" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await Counts(db));
        var otherPage = await Factory.As(other.Viewer).GetStringAsync("/toolbox");
        Assert.DoesNotContain("confidential", otherPage);
        Assert.DoesNotContain("internal.private.example", otherPage);
        var refreshed = await client.GetAsync("/toolbox");
        Assert.True(refreshed.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("confidential", await refreshed.Content.ReadAsStringAsync());
    }

    static async Task<string> Counts(FullyQualified.Web.AppDbContext db) => string.Join(",", new[]
    {
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.Asset>()),
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.CheckResult>()),
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.AlertLogEntry>()),
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.Job>()),
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.CertificateRecord>()),
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AcrossTenants<FullyQualified.Web.Change>())
    });
}

public class ToolboxErrorPrivacyTests
{
    [Fact]
    public async Task Transport_failures_do_not_echo_or_log_diagnostic_material()
    {
        var logs = new TestLogCapture();
        using var original = new RoleFactory();
        using var app = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<FullyQualified.Web.ToolNetwork>(new FailingNetwork());
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
        }));
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Role", "Viewer");
        await ToolboxEndpointTests.Token(client);
        var response = await client.PostAsJsonAsync("/api/toolbox", new { tool = "dns", host = "confidential-diagnostic.example" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("confidential-diagnostic", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(logs.Messages, text => text.Contains("confidential-diagnostic", StringComparison.Ordinal));
    }

    sealed class FailingNetwork() : FullyQualified.Web.ToolNetwork(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
    {
        public override Task<DnsClient.IDnsQueryResponse> QueryAsync(string name, DnsClient.QueryType type, CancellationToken ct)
            => throw new IOException("confidential-diagnostic.example: supplied target was unreachable");
    }

    sealed class TestLogCapture : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> entries) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => entries.Enqueue(formatter(state, exception) + exception);
        }
    }
}

public class ToolboxEarlyRejectionTests(RoleFactory factory) : IClassFixture<RoleFactory>
{
    [Fact]
    public async Task Non_json_uploads_are_rejected_before_antiforgery_form_processing()
    {
        var spy = new FormSpy();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FormSpyStartup(spy))));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Role", "Viewer");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[70_000]), "file", "certificate.pem");
        var response = await client.PostAsync("/api/toolbox", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, spy.Reads);
    }

    [Fact]
    public async Task Oversized_requests_are_rejected_before_token_or_body_processing()
    {
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { tool = "headers", text = new string('a', 500_000) }), System.Text.Encoding.UTF8, "application/json");
        content.Headers.ContentLength = (await content.ReadAsByteArrayAsync()).Length;
        var response = await factory.As("Viewer").PostAsync("/api/toolbox", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}

internal sealed class FormSpy : Microsoft.AspNetCore.Http.Features.IFormFeature
{
    public int Reads;
    public bool HasFormContentType => true;
    public Microsoft.AspNetCore.Http.IFormCollection? Form { get; set; }
    public Microsoft.AspNetCore.Http.IFormCollection ReadForm()
    {
        Reads++;
        return new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
    }
    public Task<Microsoft.AspNetCore.Http.IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromResult(ReadForm());
}
internal sealed class FormSpyStartup(FormSpy spy) : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, continuation) =>
        {
            if (context.Request.Path == "/api/toolbox") context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(spy);
            await continuation();
        });
        next(app);
    };
}
