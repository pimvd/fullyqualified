using System.Net;
using FullyQualified.Web;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FullyQualified.Tests;

/// <summary>Signs every request in as the role named by the X-Role header, so authorization can be tested without a database.</summary>
public class RoleFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:AutoMigrate", "false");
        builder.UseSetting("Scanning:Enabled", "false");
        builder.ConfigureServices(s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderRoleHandler>("Test", _ => { });
            s.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = "Test");
        });
    }

    public HttpClient As(string role)
    {
        var client = CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Role", role);
        return client;
    }

    class HeaderRoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }
}

public class RoleTests(RoleFactory factory) : IClassFixture<RoleFactory>
{
    [Theory]
    [InlineData("Viewer")]
    [InlineData("Member")]
    public async Task Only_admins_reach_people_management(string role)
    {
        var client = factory.As(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/admin/invites", new { email = "a@b.nl", role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/admin/users/{Guid.NewGuid()}/role", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/admin/users/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/admin/invites/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_to_admin_endpoints_get_401()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/admin/invites", new { })).StatusCode);
    }

    [Fact]
    public void Roles_are_cumulative()
    {
        static ClaimsPrincipal As(Role r) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, r.ToString())], "t"));
        Assert.False(Roles.CanEdit(As(Role.Viewer)));
        Assert.True(Roles.CanEdit(As(Role.Member)));
        Assert.True(Roles.CanEdit(As(Role.Admin)));
        Assert.False(Roles.IsAdmin(As(Role.Member)));
        Assert.True(Roles.IsAdmin(As(Role.Admin)));
        Assert.False(Roles.CanEdit(null));
    }
}

public class SsoPolicyTests
{
    [Fact]
    public void Allowed_domains_decide_when_configured()
    {
        var domains = SsoPolicy.Domains("Example.nl; @corp.example.com");
        Assert.True(SsoPolicy.Trusted("a@example.nl", null, domains));
        Assert.True(SsoPolicy.Trusted("a@CORP.example.com", "false", domains));
        Assert.False(SsoPolicy.Trusted("a@evil.example", "true", domains));
        Assert.False(SsoPolicy.Trusted("a@sub.example.nl", "true", domains));
    }

    [Fact]
    public void Without_domains_the_provider_must_verify_the_address()
    {
        Assert.True(SsoPolicy.Trusted("a@example.nl", "true", []));
        Assert.False(SsoPolicy.Trusted("a@example.nl", "false", []));
        Assert.False(SsoPolicy.Trusted("a@example.nl", null, []));
        Assert.False(SsoPolicy.Trusted("not-an-address", "true", []));
    }

    [Theory]
    [InlineData("  Bob@Example.NL ", "bob@example.nl")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not an address", "")]
    [InlineData("a@", "")]
    public void Email_is_normalized_or_rejected(string? input, string? expected) =>
        Assert.Equal(expected, AdminService.NormalizeEmail(input));
}
