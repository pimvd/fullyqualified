using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Tests;

public class EmailTestAccessTests(RoleFactory factory) : IClassFixture<RoleFactory>
{
    [Fact]
    public async Task Sending_requires_sign_in()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/admin/email-test", new { })).StatusCode);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Member")]
    public async Task Only_admins_can_send_test_emails(string role)
    {
        var client = factory.As(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/email-test")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/admin/email-test", new { })).StatusCode);
    }
}

public class EmailTestValidationTests
{
    static EmailTestRequest Valid => new("sender@example.org", "recipient@example.net", "Authentication test", "Check the original headers.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("sender@localhost")]
    [InlineData("Name <sender@example.org>")]
    [InlineData("sender@example.org\r\nBcc: other@example.net")]
    [InlineData("one@example.org,two@example.org")]
    public void Rejects_invalid_or_multiple_addresses(string? address)
    {
        Assert.Equal("invalid_from", EmailTestEndpoints.Validate(Valid with { From = address }));
        Assert.Equal("invalid_to", EmailTestEndpoints.Validate(Valid with { To = address }));
    }

    [Fact]
    public void Rejects_header_injection_and_empty_or_oversized_content()
    {
        Assert.Equal("invalid_subject", EmailTestEndpoints.Validate(Valid with { Subject = "Test\r\nBcc: other@example.net" }));
        Assert.Equal("invalid_subject", EmailTestEndpoints.Validate(Valid with { Subject = new string('a', 201) }));
        Assert.Equal("invalid_subject", EmailTestEndpoints.Validate(Valid with { Subject = " " }));
        Assert.Equal("invalid_message", EmailTestEndpoints.Validate(Valid with { Message = new string('a', 10001) }));
        Assert.Equal("invalid_message", EmailTestEndpoints.Validate(Valid with { Message = " " }));
        Assert.Null(EmailTestEndpoints.Validate(Valid with { Message = "Line one\r\nLine two" }));
    }

    [Fact]
    public async Task Explicit_from_address_and_all_message_fields_reach_smtp()
    {
        using var smtp = new TestSmtpServer();
        using var factory = new NoDbFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Alerts:Email:Host", "127.0.0.1");
            builder.UseSetting("Alerts:Email:Port", smtp.Port.ToString());
            builder.UseSetting("Alerts:Email:Ssl", "false");
            builder.UseSetting("Alerts:Email:From", "configured@example.org");
        });
        using var scope = factory.Services.CreateScope();
        var mail = scope.ServiceProvider.GetRequiredService<AlertService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await mail.MailAsync("custom@example.org", "receiver@example.net", "DMARC test", "Custom message body", timeout.Token);
        var received = await smtp.Received.Task.WaitAsync(timeout.Token);
        Assert.Contains("MAIL FROM:<custom@example.org>", received);
        Assert.Contains("RCPT TO:<receiver@example.net>", received);
        Assert.Contains("From: custom@example.org", received);
        Assert.Contains("To: receiver@example.net", received);
        Assert.Contains("Subject: DMARC test", received);
        Assert.Contains("Custom message body", received);
        Assert.DoesNotContain("configured@example.org", received);
    }
}

/// <summary>A loopback SMTP sink: accepts one message and captures the envelope and DATA actually sent on the wire.</summary>
public sealed class TestSmtpServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
    public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool Reject { get; init; }

    public TestSmtpServer()
    {
        listener.Start();
        _ = Serve();
    }

    async Task Serve()
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost SMTP test");
            var transcript = new System.Text.StringBuilder();
            var data = false;
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                transcript.AppendLine(line);
                if (data)
                {
                    if (line != ".") continue;
                    data = false;
                    await writer.WriteLineAsync("250 Message accepted");
                    Received.TrySetResult(transcript.ToString());
                }
                else if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 localhost");
                else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) && Reject)
                {
                    await writer.WriteLineAsync("550 Sender rejected: secret-server-detail");
                    Received.TrySetResult(transcript.ToString());
                }
                else if (line == "DATA") { data = true; await writer.WriteLineAsync("354 End with a dot"); }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                else await writer.WriteLineAsync("250 OK");
            }
        }
        catch (Exception ex) { Received.TrySetException(ex); }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
}

public class EmailTestIntegrationTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    static object Request(string from = "sender@example.org") => new { from, to = "recipient@example.net", subject = "DMARC delivery check", message = "Inspect the original message headers." };

    void ConfigureSmtp(TestSmtpServer? smtp)
    {
        var config = Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        config["Alerts:Email:Host"] = smtp is null ? "" : "127.0.0.1";
        config["Alerts:Email:Port"] = smtp?.Port.ToString();
        config["Alerts:Email:Ssl"] = "false";
        config["Alerts:Email:From"] = "configured@example.org";
    }

    /// <summary>Adds a monitored domain, so a From address on it (or a subdomain of it) passes the ownership check.</summary>
    async Task AddDomain(Guid tenant, string name)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Assets.Add(new Asset { TenantId = tenant, Name = name, Kind = AssetKind.Domain });
        await db.SaveChangesAsync();
    }

    [DbFact]
    public async Task Admin_can_send_a_real_message_and_the_attempt_is_logged_for_their_tenant()
    {
        var org = await NewOrg();
        await AddDomain(org.Id, "example.org");
        using var smtp = new TestSmtpServer();
        ConfigureSmtp(smtp);
        var admin = Factory.As(org.Admin);
        var page = await admin.GetStringAsync("/email-test");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(page);
        Assert.Equal("configured@example.org", document.QuerySelector("input[name=from]")?.GetAttribute("value"));
        Assert.NotNull(document.QuerySelector("input[name=to][type=email][required]"));
        Assert.NotNull(document.QuerySelector("input[name=subject][required]"));
        Assert.NotNull(document.QuerySelector("textarea[name=message][required]"));
        Assert.Null(document.QuerySelector("button[type=submit][disabled]"));

        var response = await admin.PostAsJsonAsync("/admin/email-test", Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("accepted").GetBoolean());
        var wire = await smtp.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("MAIL FROM:<sender@example.org>", wire);
        Assert.Contains("RCPT TO:<recipient@example.net>", wire);
        Assert.Contains("Subject: DMARC delivery check", wire);
        Assert.Contains("Inspect the original message headers.", wire);

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.AcrossTenants<AlertLogEntry>().SingleAsync(x => x.TenantId == org.Id);
        Assert.True(log.Success);
        Assert.Equal("email-test", log.Channel);
        Assert.Equal("DMARC delivery check", log.Subject);
        var tenantScope = scope.ServiceProvider.GetRequiredService<TenantScope>();
        tenantScope.All = false;
        tenantScope.Id = org.Id;
        var report = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter());
        Assert.Equal(0, report.AlertsSent);
        Assert.Contains(report.Alerts, a => a.Id == log.Id);
        Assert.Contains("SMTP accepted", await admin.GetStringAsync("/log"));
    }

    [DbFact]
    public async Task Relay_rejection_is_logged_without_exposing_the_smtp_reply()
    {
        var org = await NewOrg();
        await AddDomain(org.Id, "example.org");
        using var smtp = new TestSmtpServer { Reject = true };
        ConfigureSmtp(smtp);
        var response = await Factory.As(org.Admin).PostAsJsonAsync("/admin/email-test", Request());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("smtp_failed", (await Json(response)).GetProperty("error").GetString());
        Assert.DoesNotContain("secret-server-detail", await response.Content.ReadAsStringAsync());
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.AcrossTenants<AlertLogEntry>().SingleAsync(x => x.TenantId == org.Id);
        Assert.False(log.Success);
        Assert.DoesNotContain("secret-server-detail", log.Error);

        db.AlertSettings.Add(new AlertSettings { TenantId = org.Id, EmailTo = "ops@example.org" });
        await db.SaveChangesAsync();
        var diagnostics = await Factory.As(org.Admin).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false });
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        var items = (await Json(diagnostics)).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(items, i => i.GetProperty("title").GetString() == "Recent alarms");

        var tenantScope = scope.ServiceProvider.GetRequiredService<TenantScope>();
        tenantScope.All = false;
        tenantScope.Id = org.Id;
        var report = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter());
        Assert.Equal(0, report.AlertsFailed);
        Assert.Contains(report.Alerts, a => a.Id == log.Id);

        db.AlertLog.Add(new AlertLogEntry { TenantId = org.Id, Channel = "email", Subject = "Production alarm", Success = false });
        await db.SaveChangesAsync();
        diagnostics = await Factory.As(org.Admin).PostAsJsonAsync("/admin/diagnostics", new { sendAlarm = false });
        Assert.Contains((await Json(diagnostics)).GetProperty("items").EnumerateArray(), i => i.GetProperty("title").GetString() == "Recent alarms");
        report = await scope.ServiceProvider.GetRequiredService<LogQueries>().LoadAsync(new LogFilter());
        Assert.Equal(1, report.AlertsFailed);
    }

    [DbFact]
    public async Task Invalid_input_and_missing_smtp_do_not_send_mail()
    {
        var org = await NewOrg();
        ConfigureSmtp(null);
        var admin = Factory.As(org.Admin);
        var invalid = await admin.PostAsJsonAsync("/admin/email-test", Request("bad\r\nBcc: other@example.org"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_from", (await Json(invalid)).GetProperty("error").GetString());
        var missingSmtp = await admin.PostAsJsonAsync("/admin/email-test", Request());
        Assert.Equal(HttpStatusCode.Conflict, missingSmtp.StatusCode);
        Assert.Equal("no_smtp", (await Json(missingSmtp)).GetProperty("error").GetString());
        var page = await admin.GetStringAsync("/email-test");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(page);
        Assert.NotNull(document.QuerySelector("button[type=submit][disabled]"));
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AcrossTenants<AlertLogEntry>().AnyAsync(x => x.TenantId == org.Id));
    }

    [DbFact]
    public async Task The_From_address_must_be_on_a_domain_this_tenant_monitors()
    {
        var org = await NewOrg();
        await AddDomain(org.Id, "example.org");
        using var smtp = new TestSmtpServer();
        ConfigureSmtp(smtp);
        var admin = Factory.As(org.Admin);

        // Not monitored at all: refused before anything is sent.
        var unowned = await admin.PostAsJsonAsync("/admin/email-test", Request("sender@not-ours.example"));
        Assert.Equal(HttpStatusCode.Forbidden, unowned.StatusCode);
        Assert.Equal("from_not_owned", (await Json(unowned)).GetProperty("error").GetString());

        // A lookalike host (lookss like a suffix but is a different domain) must not pass.
        var lookalike = await admin.PostAsJsonAsync("/admin/email-test", Request("sender@notexample.org"));
        Assert.Equal("from_not_owned", (await Json(lookalike)).GetProperty("error").GetString());

        // A subdomain of a monitored domain is allowed.
        var subdomain = await admin.PostAsJsonAsync("/admin/email-test", Request("sender@mail.example.org"));
        Assert.Equal(HttpStatusCode.OK, subdomain.StatusCode);
        await smtp.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Another tenant's monitored domain does not grant access either.
        var other = await NewOrg();
        await AddDomain(other.Id, "other-example.org");
        var crossTenant = await admin.PostAsJsonAsync("/admin/email-test", Request("sender@other-example.org"));
        Assert.Equal("from_not_owned", (await Json(crossTenant)).GetProperty("error").GetString());

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AcrossTenants<AlertLogEntry>().CountAsync(x => x.TenantId == org.Id)); // only the accepted send was logged
    }

    [DbFact]
    public async Task Sending_is_limited_per_tenant_without_blocking_other_tenants()
    {
        var org = await NewOrg();
        var other = await NewOrg();
        var admin = Factory.As(org.Admin);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/email-test", Request("bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await admin.PostAsJsonAsync("/admin/email-test", Request("bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Factory.As(other.Admin).PostAsJsonAsync("/admin/email-test", Request("bad"))).StatusCode);
    }
}
