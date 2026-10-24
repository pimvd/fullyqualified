using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class DigTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("_dmarc.example.com", "_dmarc.example.com")]
    [InlineData("  8.8.8.8 ", "8.8.8.8")]
    [InlineData("2001:4860:4860::8888", "2001:4860:4860::8888")]
    [InlineData("localhost", "localhost")]
    [InlineData("", null)]
    [InlineData("not a name", null)]
    [InlineData("-bad.example.com", null)]
    [InlineData("a..b.example.com", null)]
    public void Names_are_normalized_or_refused(string input, string? expected) => Assert.Equal(expected, DigService.NormalizeName(input));

    [Fact]
    public void Resolvers_start_with_the_system_resolver_followed_by_the_configured_ones()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dns:CompareResolvers"] = "1.1.1.1, 9.9.9.9, not-an-ip" }).Build();
        var keys = new DigService(cfg).Resolvers.Select(r => r.Key);
        Assert.Equal(["system", "1.1.1.1", "9.9.9.9"], keys);
        Assert.Equal(["system"], new DigService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dns:CompareResolvers"] = "" }).Build()).Resolvers.Select(r => r.Key));
    }

    [Fact]
    public void A_user_is_limited_to_sixty_lookups_a_minute()
    {
        var dig = new DigService(new ConfigurationBuilder().Build());
        Assert.All(Enumerable.Range(0, 60), _ => Assert.True(dig.Allow("u1")));
        Assert.False(dig.Allow("u1"));
        Assert.True(dig.Allow("u2"));
    }

    [DbFact]
    public async Task The_lookup_needs_a_signed_in_user_and_refuses_bad_input()
    {
        var org = await NewOrg();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).GetAsync("/api/dig?name=example.com")).StatusCode);

        var user = Factory.As(org.Admin);
        async Task<(HttpStatusCode Status, string? Error)> Ask(string query)
        {
            var response = await user.GetAsync("/api/dig?" + query);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response.StatusCode, body.TryGetProperty("error", out var e) ? e.GetString() : null);
        }
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_name"), await Ask("name=not%20a%20name"));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_name"), await Ask("type=A"));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_type"), await Ask("name=example.com&type=ANY"));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_resolver"), await Ask("name=example.com&resolver=10.0.0.1")); // only the listed resolvers can be asked
    }

    [DbFact]
    public async Task The_overlay_and_its_open_button_are_on_every_page_for_signed_in_users_only()
    {
        var org = await NewOrg();
        var page = await (await Factory.As(org.Admin).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"dig\"", page);
        Assert.Contains("data-dig-open", page);
        Assert.Contains("data-dig-type=\"TXT\"", page);
        Assert.Contains("System resolver", page);
        Assert.Contains("dig.js", page);

        var anonymous = await (await Factory.As(null).GetAsync("/login")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"dig\"", anonymous);
    }

    [Fact]
    public void An_empty_answer_shows_only_that_there_are_no_records_never_the_zones_SOA()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullyQualified.slnx"))) dir = dir.Parent;
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "src", "FullyQualified.Web", "wwwroot", "dig.js"));
        Assert.Contains("nodata", script);                                   // the plain message is there ...
        Assert.DoesNotContain("r.authority", script);                        // ... and the authority section is never drawn
    }

    [DbFact]
    public async Task Every_screen_has_a_refresh_button_signed_in_or_not()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        foreach (var path in new[] { "/", "/review", "/certificates", "/changes", "/reports", "/log", "/alerts", "/users", "/organization", "/account", "/does-not-exist" })
            Assert.True((await (await admin.GetAsync(path)).Content.ReadAsStringAsync()).Contains("data-refresh"), $"{path} has no refresh button");
        Assert.True((await (await Factory.As(null).GetAsync("/login")).Content.ReadAsStringAsync()).Contains("data-refresh"), "/login has no refresh button");

        // A URL that does not exist is a real screen too, still answering 404, for anyone.
        foreach (var client in new[] { admin, Factory.As(null) })
        {
            var missing = await client.GetAsync("/no/such/page");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var body = await missing.Content.ReadAsStringAsync();
            Assert.Contains("Page not found", body);
            Assert.Contains("data-refresh", body);
        }
    }
}
