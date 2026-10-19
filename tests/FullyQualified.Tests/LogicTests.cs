using Microsoft.Extensions.Configuration;
using FullyQualified.Web;

namespace FullyQualified.Tests;

public class LogicTests
{
    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("https://example.com/path?q=1", "example.com")]
    [InlineData("host.example.com:8443", "host.example.com:8443")]
    [InlineData("example.com.", "example.com")]
    [InlineData("not a host", null)]
    [InlineData("localhost", null)]
    [InlineData("example.com:99999", null)]
    public void HostName_normalizes(string input, string? expected) => Assert.Equal(expected, HostName.Normalize(input));

    [Fact]
    public void Diff_reports_changed_added_and_removed_keys_and_skips_internal_ones()
    {
        var before = new Dictionary<string, string> { ["mx"] = "10 a", ["ns"] = "x", ["_issues"] = "" };
        var after = new Dictionary<string, string> { ["mx"] = "10 b", ["spf"] = "v=spf1", ["_issues"] = "NoCaa" };
        var diff = ChangeDetector.Diff(before, after);
        Assert.Equal(["mx", "ns", "spf"], diff.Select(d => d.Key));
    }

    [Fact]
    public void Diff_without_baseline_reports_nothing() =>
        Assert.Empty(ChangeDetector.Diff(null, new Dictionary<string, string> { ["mx"] = "10 a" }));

    [Fact]
    public void Renewal_guess_detects_acme_and_short_lived()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Automated", Renewal.Guess("CN=R11, O=Let's Encrypt, C=US", now, now.AddDays(90)));
        Assert.Equal("Automated", Renewal.Guess("CN=Corp CA", now, now.AddDays(47)));
        Assert.Equal("Manual", Renewal.Guess("CN=Corp CA", now, now.AddDays(365)));
    }

    [Fact]
    public void Status_ordering_makes_critical_the_worst() =>
        Assert.Equal(CheckStatus.Critical, new[] { CheckStatus.Ok, CheckStatus.Critical, CheckStatus.Warning }.Max());

    [Fact]
    public async Task HostGuard_blocks_loopback_when_private_addresses_are_disallowed()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scanning:AllowPrivateAddresses"] = "false" }).Build();
        Assert.False(await new HostGuard(cfg).IsAllowedAsync("127.0.0.1", default));
    }

    [Fact]
    public void Reports_render_to_xlsx_and_pdf()
    {
        var model = new ReportModel(DateTimeOffset.UtcNow, 1, 1, 0, 0,
            [new("example.com", true, false, false, true)],
            [new("example.com", "R11", DateTimeOffset.UtcNow.AddDays(60), 60, "endpoint", "Automated")],
            [new(DateTimeOffset.UtcNow, "example.com", "dns", "mx", "10 a", "10 b")]);
        var l = new KeyLocalizer();
        var xlsx = ReportWriter.Excel(model, l);
        var pdf = ReportWriter.Pdf(model, l);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(xlsx, 0, 2));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
