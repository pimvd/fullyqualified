using System.Net.Http.Json;

namespace FullyQualified.Tests;

public class HealthTests(NoDbFactory factory) : IClassFixture<NoDbFactory>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Culture_endpoint_ignores_external_redirects()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/culture?culture=nl&redirect=//evil.example");
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }
}

public class AuthTests(NoDbFactory factory) : IClassFixture<NoDbFactory>
{
    [Fact]
    public async Task Pages_and_reports_require_sign_in()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var path in new[] { "/", "/reports/nis2.pdf", "/reports/nis2.xlsx", "/account" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
        }
    }

    [Fact]
    public async Task Account_endpoints_answer_401_without_a_session()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var path in new[] { "/auth/invites", "/auth/passkeys/options", "/auth/logout" })
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/auth/passkeys/{Guid.NewGuid()}")).StatusCode);
    }
}
