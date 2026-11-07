using System.Net;
using DnsClient;
using DnsClient.Protocol;
using System.Net;
using FullyQualified.Web;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class SpfTreeTests
{
    static ResourceRecordInfo Info(string name, ResourceRecordType type) => new(name, type, QueryClass.IN, 60, 0);

    static ToolboxMailTests.FakeNetwork Network(params (string Domain, string Record)[] spf)
    {
        var network = new ToolboxMailTests.FakeNetwork();
        foreach (var (domain, record) in spf) network.Txt(domain, record);
        return network;
    }

    static void A(ToolboxMailTests.FakeNetwork n, string name, params string[] ips) =>
        n.Records[(name, QueryType.A)] = ips.Select(ip => (DnsResourceRecord)new ARecord(Info(name, ResourceRecordType.A), IPAddress.Parse(ip))).ToArray();

    static void Aaaa(ToolboxMailTests.FakeNetwork n, string name, params string[] ips) =>
        n.Records[(name, QueryType.AAAA)] = ips.Select(ip => (DnsResourceRecord)new AaaaRecord(Info(name, ResourceRecordType.AAAA), IPAddress.Parse(ip))).ToArray();

    static void Mx(ToolboxMailTests.FakeNetwork n, string name, params (ushort Pref, string Host)[] hosts) =>
        n.Records[(name, QueryType.MX)] = hosts.Select(h => (DnsResourceRecord)new MxRecord(Info(name, ResourceRecordType.MX), h.Pref, DnsString.Parse(h.Host))).ToArray();

    static Task<SpfTree> Build(ToolboxMailTests.FakeNetwork n, string domain = "example.com") => new SpfTreeBuilder(n).BuildAsync(domain, default);

    static IEnumerable<SpfNode> All(SpfNode node) => new[] { node }.Concat(node.Children.SelectMany(All));

    [Fact]
    public async Task Includes_are_followed_down_to_their_addresses()
    {
        var n = Network(("example.com", "v=spf1 ip4:192.0.2.0/24 include:_spf.mail.test -all"),
                        ("_spf.mail.test", "v=spf1 ip4:198.51.100.7 ip6:2001:db8::/32 include:deeper.test ~all"),
                        ("deeper.test", "v=spf1 ip4:203.0.113.9 -all"));
        var tree = await Build(n);

        Assert.Equal("domain", tree.Root.Kind);
        Assert.Equal(["ip4", "include", "all"], tree.Root.Children.Select(c => c.Kind));
        Assert.Equal("192.0.2.0/24", tree.Root.Children[0].Text);
        var include = tree.Root.Children[1];
        Assert.Equal(("_spf.mail.test", "+"), (include.Text, include.Qualifier));
        Assert.Equal(["ip4", "ip6", "include", "all"], include.Children.Select(c => c.Kind));
        Assert.Equal("203.0.113.9", All(include).Single(x => x.Text == "203.0.113.9").Text);
        Assert.Equal(2, tree.Lookups);
        Assert.False(tree.Truncated);
    }

    [Fact]
    public async Task A_and_mx_are_resolved_to_hosts_and_addresses()
    {
        var n = Network(("example.com", "v=spf1 a mx:mail.test/28 -all"));
        A(n, "example.com", "192.0.2.10");
        Mx(n, "mail.test", (20, "mx2.mail.test."), (10, "mx1.mail.test."));
        A(n, "mx1.mail.test", "198.51.100.1");
        Aaaa(n, "mx1.mail.test", "2001:db8::1");
        A(n, "mx2.mail.test", "198.51.100.2");
        var tree = await Build(n);

        var a = tree.Root.Children[0];
        Assert.Equal(("a", "example.com"), (a.Kind, a.Text));
        Assert.Equal(["192.0.2.10"], a.Children.Select(c => c.Text));
        var mx = tree.Root.Children[1];
        Assert.Equal("mail.test/28", mx.Text);
        Assert.Equal(["mx1.mail.test", "mx2.mail.test"], mx.Children.Select(c => c.Text));    // lowest preference first
        Assert.Equal(["198.51.100.1/28", "2001:db8::1"], mx.Children[0].Children.Select(c => c.Text));   // /28 is the IPv4 length only
        Assert.Equal(2, tree.Lookups);
    }

    [Fact]
    public async Task Each_prefix_length_goes_with_its_own_address_family()
    {
        var n = Network(("example.com", "v=spf1 a/24//64 -all"));
        A(n, "example.com", "192.0.2.10");
        Aaaa(n, "example.com", "2001:db8::10");
        var children = (await Build(n)).Root.Children[0].Children.Select(c => c.Text).ToList();
        Assert.Equal(["192.0.2.10/24", "2001:db8::10/64"], children);
    }

    [Fact]
    public async Task A_redirect_is_followed_unless_the_record_already_ends_in_all()
    {
        var n = Network(("example.com", "v=spf1 redirect=other.test"), ("other.test", "v=spf1 ip4:192.0.2.1 -all"),
                        ("done.test", "v=spf1 -all redirect=other.test"));
        var redirected = (await Build(n)).Root.Children.Single();
        Assert.Equal(("redirect", "other.test"), (redirected.Kind, redirected.Text));
        Assert.Equal("192.0.2.1", redirected.Children[0].Text);
        Assert.DoesNotContain(All((await Build(n, "done.test")).Root), x => x.Kind == "redirect");
    }

    [Fact]
    public async Task A_loop_is_marked_and_not_followed()
    {
        var n = Network(("example.com", "v=spf1 include:b.test -all"), ("b.test", "v=spf1 include:example.com -all"));
        var tree = await Build(n);
        var back = All(tree.Root).Single(x => x.Note == "loop");
        Assert.Equal("example.com", back.Text);
        Assert.Empty(back.Children);
    }

    [Fact]
    public async Task Problems_are_shown_on_the_node_they_belong_to()
    {
        var n = Network(("example.com", "v=spf1 include:missing.test include:two.test include:%{d}.test ptr exists:%{i}.x.test bogus! -all"));
        n.Txt("two.test", "v=spf1 -all", "v=spf1 +all");
        var nodes = (await Build(n)).Root.Children.ToDictionary(c => c.Text + "|" + c.Kind);
        Assert.Equal("no_record", nodes["missing.test|include"].Note);
        Assert.Equal("multiple", nodes["two.test|include"].Note);
        Assert.Equal("macro", nodes["%{d}.test|include"].Note);
        Assert.Equal("ptr", nodes["example.com|ptr"].Note);
        Assert.Equal("exists", nodes["%{i}.x.test|exists"].Note);
        Assert.Contains(nodes.Values, x => x.Kind == "unknown" && x.Text == "bogus!");
    }

    [Fact]
    public async Task A_missing_record_at_the_top_is_a_single_node_with_a_note()
    {
        var tree = await Build(Network());
        Assert.Equal("no_record", tree.Root.Note);
        Assert.Empty(tree.Root.Children);
    }

    [Fact]
    public async Task Runaway_records_stop_at_the_limits()
    {
        // 30 includes each pointing at a record with 30 addresses is far more than the node limit.
        var n = new ToolboxMailTests.FakeNetwork();
        n.Txt("example.com", "v=spf1 " + string.Join(' ', Enumerable.Range(0, 30).Select(i => $"include:i{i}.test")) + " -all");
        for (var i = 0; i < 30; i++) n.Txt($"i{i}.test", "v=spf1 " + string.Join(' ', Enumerable.Range(0, 30).Select(j => $"ip4:10.{i}.{j}.1")) + " -all");
        var tree = await Build(n);
        Assert.True(tree.Truncated);
        Assert.True(All(tree.Root).Count() <= SpfTreeBuilder.MaxNodes + 1);
        Assert.True(n.Queries.Count <= SpfTreeBuilder.MaxQueries);
    }

    [Fact]
    public async Task Nesting_stops_at_the_depth_limit()
    {
        var n = new ToolboxMailTests.FakeNetwork();
        for (var i = 0; i < 20; i++) n.Txt($"d{i}.test", $"v=spf1 include:d{i + 1}.test -all");
        var tree = await Build(n, "d0.test");
        Assert.True(tree.Truncated);
        Assert.Contains(All(tree.Root), x => x.Note == "depth");
    }

    [Theory]
    [InlineData("include:1.2.3.4")]          // an address is not a name to look up
    [InlineData("include:bad_host!.test")]
    public async Task Names_that_are_not_dns_names_are_not_looked_up(string term)
    {
        var n = Network(("example.com", $"v=spf1 {term} -all"));
        var tree = await Build(n);
        Assert.Equal("macro", tree.Root.Children[0].Note);
        Assert.Equal(["example.com"], n.Queries);          // only the record itself was asked for
    }
}

public class SpfTreeEndpointTests(PgFactory factory) : OrgTestBase<PgFactory>(factory), IClassFixture<PgFactory>
{
    async Task<Asset> AssetWithSpf(Guid tenant)
    {
        var asset = await AddAsset(tenant, $"s.{tenant:N}.test.com");
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Results.Add(new CheckResult { TenantId = tenant, AssetId = asset.Id, CheckType = "dns", Status = CheckStatus.Ok, Summary = "Sum_DnsOk", ValuesJson = "{\"spf\":\"v=spf1 include:_spf.example.net -all\"}" });
        await db.SaveChangesAsync();
        return asset;
    }

    [DbFact]
    public async Task The_asset_page_offers_the_tree_next_to_the_spf_record_with_its_texts_from_the_resource_files()
    {
        var org = await NewOrg();
        var asset = await AssetWithSpf(org.Id);
        var page = await Factory.As(org.Viewer).GetStringAsync($"/assets/{asset.Id}");
        Assert.Contains("details class=\"spf-tree\"", page);
        Assert.Contains($"data-url=\"/assets/{asset.Id}/spf-tree\"", page);
        Assert.Contains("SPF tree", page);
        Assert.Contains("DNS lookups (the limit is 10)", page);       // the labels reach the script
        Assert.Contains("/spf-tree.js", page);
    }

    [DbFact]
    public async Task The_tree_needs_a_session_and_never_crosses_organizations()
    {
        var org = await NewOrg();
        var other = await NewOrg();
        var theirs = await AssetWithSpf(other.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).GetAsync($"/assets/{theirs.Id}/spf-tree")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.As(org.Admin).GetAsync($"/assets/{theirs.Id}/spf-tree")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.As(org.Admin).GetAsync($"/assets/{Guid.NewGuid()}/spf-tree")).StatusCode);
    }
}
