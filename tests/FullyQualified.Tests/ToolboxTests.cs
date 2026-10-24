using System.Net;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class ToolboxTests
{
    [Theory]
    [InlineData("[::1]", "::1")]
    [InlineData(" EXAMPLE.COM. ", "example.com")]
    [InlineData("internal", "internal")]
    [InlineData("https://example.com", null)]
    [InlineData("host/path", null)]
    public void Target_normalization_supports_ips_and_internal_names_without_accepting_urls(string input, string? expected)
        => Assert.Equal(expected, ToolNetwork.NormalizeHost(input));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    [InlineData("10.0.0.1")]
    public async Task Strict_network_policy_blocks_private_addresses(string ip)
    {
        var network = new ToolNetwork(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Scanning:AllowPrivateAddresses"] = "false" }).Build());
        await Assert.ThrowsAsync<ToolException>(() => network.GuardAddressesAsync([IPAddress.Parse(ip)], CancellationToken.None));
    }
}

public class ToolboxLimitTests
{
    [Fact]
    public void Concurrent_limits_are_per_user_and_process_and_disposal_releases_once()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Toolbox:ConcurrentPerUser"] = "1", ["Toolbox:Concurrent"] = "2", ["Toolbox:RequestsPerMinute"] = "2"
        }).Build();
        var limits = new ToolLimits(config);
        var first = limits.Enter("first");
        Assert.NotNull(first);
        Assert.Null(limits.Enter("first"));
        using var second = limits.Enter("second");
        Assert.NotNull(second);
        Assert.Null(limits.Enter("third"));
        first.Dispose(); first.Dispose();
        using var retry = limits.Enter("first");
        Assert.NotNull(retry);
        retry.Dispose();
        Assert.Null(limits.Enter("first"));
        using var available = limits.Enter("third");
        Assert.NotNull(available);
    }

    [Fact]
    public async Task A_public_address_does_not_allow_a_private_secondary_address()
    {
        var network = new ToolNetwork(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Scanning:AllowPrivateAddresses"] = "false" }).Build());
        await Assert.ThrowsAsync<ToolException>(() => network.GuardAddressesAsync([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("::ffff:10.0.0.1")], CancellationToken.None));
    }
}

public class ToolboxHttpsPolicyTests
{
    [Fact]
    public void Policy_fetch_validation_cannot_download_unguarded_issuer_certificates()
    {
        var factory = typeof(ToolNetwork).GetMethod("CreatePolicyHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(factory);
        using var handler = Assert.IsType<SocketsHttpHandler>(factory.Invoke(null, [new[] { IPAddress.Loopback }, 5]));
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.SslOptions.CertificateChainPolicy);
        Assert.True(handler.SslOptions.CertificateChainPolicy.DisableCertificateDownloads);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck, handler.SslOptions.CertificateChainPolicy.RevocationMode);
        Assert.NotNull(handler.ConnectCallback);
    }
}
