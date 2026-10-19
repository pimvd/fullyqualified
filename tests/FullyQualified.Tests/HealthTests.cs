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
