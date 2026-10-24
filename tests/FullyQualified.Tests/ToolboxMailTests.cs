using System.Net;
using DnsClient;
using DnsClient.Protocol;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class ToolboxMailTests
{
    [Theory]
    [InlineData("mail.example.co.uk", "example.co.uk")]
    [InlineData("a.b.ck", "a.b.ck")]
    [InlineData("www.ck", "www.ck")]
    [InlineData("x.city.kawasaki.jp", "city.kawasaki.jp")]
    [InlineData("co.uk", null)]
    [InlineData("foo.blogspot.com", "foo.blogspot.com")]
    public void Organizational_domain_uses_full_public_suffix_rules(string domain, string? expected)
        => Assert.Equal(expected, PublicSuffix.OrganizationalDomain(domain));

    [Fact]
    public void Folded_repeated_headers_are_retained_and_authentication_is_unverified()
    {
        var result = HeaderTools.Analyze("From: Alice <a@example.co.uk>\r\nReceived: from first\r\n by second\r\nReceived: from third\r\nAuthentication-Results: mx; spf=pass smtp.mailfrom=evil.co.uk;\r\n dkim=pass header.d=example.co.uk\r\nDKIM-Signature: v=1; d=example.co.uk; s=key; b=x\r\n");
        var rows = result.Sections.SelectMany(x => x.Rows).ToList();
        Assert.Equal(2, rows.Count(x => x.Label == "Tool_HeaderReceived"));
        Assert.Contains(rows, x => x.Value.Contains("from first by second"));
        Assert.Contains(rows, x => x.Explanation == "Tool_HeaderUnverified");
        Assert.DoesNotContain(rows, x => x.Status == "pass");
    }

    [Fact]
    public async Task Dkim_empty_key_is_revoked_and_duplicate_tags_are_invalid()
    {
        var network = new FakeNetwork();
        network.Txt("key._domainkey.example.com", "v=DKIM1; p=");
        var result = await new MailTools(network).RunAsync(new("dkim", Domain:"example.com", Selector:"key"), default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows), x=>x.Value == "Tool_DkimRevoked");
        network.Txt("key._domainkey.example.com", "v=DKIM1; p=AA==; p=BB==");
        result = await new MailTools(network).RunAsync(new("dkim", Domain:"example.com", Selector:"key"), default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows), x=>x.Value == "Tool_InvalidRecord");
    }

    [Fact]
    public async Task Dmarc_falls_back_to_organizational_domain_and_checks_external_authorization()
    {
        var network = new FakeNetwork();
        network.Txt("_dmarc.example.co.uk", "v=DMARC1; p=reject; sp=quarantine; rua=mailto:r@reports.test; adkim=s");
        var result = await new MailTools(network).RunAsync(new("dmarc", Domain:"a.example.co.uk"), default);
        var rows = result.Sections.SelectMany(x=>x.Rows).ToList();
        Assert.Contains(rows, x=>x.Label == "Tool_DmarcSource" && x.Value == "example.co.uk");
        Assert.Contains(rows, x=>x.Label == "Tool_DmarcEffectivePolicy" && x.Value == "quarantine");
        Assert.Contains(rows, x=>x.Value == "Tool_ReportUnauthorized");
        Assert.Contains("example.co.uk._report._dmarc.reports.test", network.Queries);
    }

    [Fact]
    public async Task Spf_cycles_are_incomplete_and_never_sender_pass()
    {
        var network = new FakeNetwork();
        network.Txt("example.com", "v=spf1 include:loop.test -all");
        network.Txt("loop.test", "v=spf1 include:example.com -all");
        var result = await new SpfTools(network).RunAsync(new("spf", Domain:"example.com", SenderIp:"192.0.2.1"), default);
        Assert.False(result.Complete);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows), x=>x.Value == "Tool_SpfCycle");
        Assert.DoesNotContain(result.Sections.SelectMany(x=>x.Rows), x=>x.Label=="Tool_SpfEvaluation" && x.Value=="pass");
    }

    [Fact]
    public async Task Spf_sender_evaluation_obeys_qualifiers_and_network_prefixes()
    {
        var network = new FakeNetwork();
        network.Txt("example.com", "v=spf1 ip4:192.0.2.0/24 -all");
        var pass = await new SpfTools(network).RunAsync(new("spf", Domain:"example.com", SenderIp:"192.0.2.7"), default);
        var fail = await new SpfTools(network).RunAsync(new("spf", Domain:"example.com", SenderIp:"198.51.100.7"), default);
        Assert.Contains(pass.Sections.SelectMany(x=>x.Rows), x=>x.Label=="Tool_SpfEvaluation" && x.Value=="pass");
        Assert.Contains(fail.Sections.SelectMany(x=>x.Rows), x=>x.Label=="Tool_SpfEvaluation" && x.Value=="fail");
    }

    [Fact]
    public async Task Mta_sts_invalid_mode_and_uncovered_mx_are_reported()
    {
        var network = new FakeNetwork { Policy = "version: STSv1\nmode: enforce\nmax_age: 100\nmx: other.example.com\n" };
        network.Txt("_mta-sts.example.com", "v=STSv1; id=123");
        network.Records[("example.com",QueryType.MX)] = [new MxRecord(new ResourceRecordInfo("example.com", ResourceRecordType.MX, QueryClass.IN, 60, 0), 10, DnsString.Parse("mx.example.com"))];
        var result = await new MailTools(network).RunAsync(new("mta-sts",Domain:"example.com"), default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows), x=>x.Label=="Tool_MtaMxCoverage" && x.Status=="warning");
        network.Policy = "version: STSv1\nmode: invalid\nmax_age: 1\n";
        result = await new MailTools(network).RunAsync(new("mta-sts",Domain:"example.com"), default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows), x=>x.Value=="Tool_InvalidRecord");
    }

    [Theory]
    [InlineData("v=spf1 a:other.test a:other.test a:other.test -all", "Tool_SpfVoidLimit")]
    [InlineData("v=spf1 include:%{d}.test -all", "Tool_SpfMacroUnknown")]
    [InlineData("v=spf1 ip4:192.0.2.0//24 +all", "Tool_SpfSyntax")]
    public async Task Incomplete_spf_does_not_claim_pass(string record,string finding)
    {
        var network=new FakeNetwork();network.Txt("example.com",record);
        var result=await new SpfTools(network).RunAsync(new("spf",Domain:"example.com",SenderIp:"192.0.2.1"),default);
        Assert.False(result.Complete);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Value==finding);
        Assert.DoesNotContain(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_SpfEvaluation"&&x.Value=="pass");
    }

    [Fact]
    public async Task Actual_spf_budget_is_not_confused_with_void_budget()
    {
        var network=new FakeNetwork();
        network.Txt("example.com","v=spf1 "+string.Join(' ',Enumerable.Repeat("a:other.test",11))+" +all");
        network.Records[("other.test",QueryType.A)]=[new ARecord(new ResourceRecordInfo("other.test",ResourceRecordType.A,QueryClass.IN,60,4),IPAddress.Parse("198.51.100.1"))];
        var result=await new SpfTools(network).RunAsync(new("spf",Domain:"example.com",SenderIp:"192.0.2.1"),default);
        Assert.False(result.Complete);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_SpfEvaluation"&&x.Value=="permerror");
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_SpfVoidLookups"&&x.Value=="0");
    }

    [Fact]
    public void Authentication_assertion_identities_are_explained_without_verifying_them()
    {
        var result=HeaderTools.Analyze("From: a@example.com\r\nAuthentication-Results: mx; spf=pass smtp.mailfrom=mail.example.com; dkim=fail header.d=unrelated.test; dmarc=pass header.from=example.com\r\n");
        var rows=result.Sections.SelectMany(x=>x.Rows).ToList();
        Assert.Contains(rows,x=>x.Label=="Tool_HeaderReportedSpf"&&x.Value=="pass"&&x.Explanation=="Tool_HeaderUnverified");
        Assert.Contains(rows,x=>x.Label=="Tool_HeaderSpfAlignment"&&x.Value=="Tool_AlignmentRelaxed");
        Assert.Contains(rows,x=>x.Label=="Tool_HeaderDkimAlignment"&&x.Value=="Tool_AlignmentNone");
    }

    [Fact]
    public async Task Dns_comparison_ignores_order_and_ttl_and_guards_discovered_nameservers()
    {
        var network=new FakeNetwork();
        network.Records[("example.com",QueryType.A)]=[new ARecord(new ResourceRecordInfo("example.com",ResourceRecordType.A,QueryClass.IN,60,4),IPAddress.Parse("192.0.2.1")),new ARecord(new ResourceRecordInfo("example.com",ResourceRecordType.A,QueryClass.IN,30,4),IPAddress.Parse("192.0.2.2"))];
        network.Records[("example.com",QueryType.NS)]=[new NsRecord(new ResourceRecordInfo("example.com",ResourceRecordType.NS,QueryClass.IN,60,0),DnsString.Parse("ns.example.com"))];
        network.Comparison=[new ARecord(new ResourceRecordInfo("EXAMPLE.COM",ResourceRecordType.A,QueryClass.IN,9,4),IPAddress.Parse("192.0.2.2")),new ARecord(new ResourceRecordInfo("example.com",ResourceRecordType.A,QueryClass.IN,900,4),IPAddress.Parse("192.0.2.1"))];
        var result=await new MailTools(network).RunAsync(new("dns",Host:"example.com"),default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Value=="Tool_DnsSame");
        Assert.True(network.Guarded>0);
    }

    [Fact]
    public async Task Spf_deadline_retains_the_already_observed_root_record()
    {
        var network=new FakeNetwork();network.Txt("example.com","v=spf1 include:slow.test -all");network.CancelQuery="slow.test";
        var result=await new SpfTools(network).RunAsync(new("spf",Domain:"example.com",SenderIp:"192.0.2.1"),default);
        Assert.False(result.Complete);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_SpfRecord"&&x.Value.Contains("example.com"));
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Value=="Tool_RunIncomplete");
    }

    [Fact]
    public async Task Policy_deadline_retains_discovery_record()
    {
        var network=new FakeNetwork{CancelPolicy=true};network.Txt("_mta-sts.example.com","v=STSv1; id=abc");
        var result=await new MailTools(network).RunAsync(new("mta-sts",Domain:"example.com"),default);
        Assert.False(result.Complete);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_MtaDiscovery"&&x.Value.Contains("id=abc"));
    }

    [Fact]
    public async Task Builder_normalizes_empty_alignment_and_rejects_invalid_mailbox()
    {
        var network=new FakeNetwork();
        var result=await new MailTools(network).RunAsync(new("dmarc-builder",Policy:"none",AlignmentSpf:"",AlignmentDkim:""),default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_Record"&&x.Value.Contains("aspf=r; adkim=r"));
        await Assert.ThrowsAsync<ToolException>(()=>new MailTools(network).RunAsync(new("dmarc-builder",Policy:"none",ReportUri:"mailto:@example.com"),default));
    }

    [Theory]
    [InlineData("mail.example.com", "info")]
    [InlineData("nested.mail.example.com", "warning")]
    public async Task Mta_wildcard_matches_exactly_one_label(string host,string status)
    {
        var network=new FakeNetwork{Policy="version: STSv1\nmode: enforce\nmax_age: 100\nmx: *.example.com\n"};
        network.Txt("_mta-sts.example.com","v=STSv1; id=abc");
        network.Records[("example.com",QueryType.MX)]=[new MxRecord(new ResourceRecordInfo("example.com",ResourceRecordType.MX,QueryClass.IN,60,0),10,DnsString.Parse(host))];
        var result=await new MailTools(network).RunAsync(new("mta-sts",Domain:"example.com"),default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_MtaMxCoverage"&&x.Value==host&&x.Status==status);
    }

    [Fact]
    public async Task Spf_redirect_modifier_is_case_insensitive()
    {
        var network=new FakeNetwork();network.Txt("example.com","v=spf1 REDIRECT=other.test");network.Txt("other.test","v=spf1 ip4:192.0.2.0/24 -all");
        var result=await new SpfTools(network).RunAsync(new("spf",Domain:"example.com",SenderIp:"192.0.2.1"),default);
        Assert.Contains(result.Sections.SelectMany(x=>x.Rows),x=>x.Label=="Tool_SpfEvaluation"&&x.Value=="pass");
    }

    public sealed class FakeNetwork() : ToolNetwork(new ConfigurationBuilder().Build())
    {
        public Dictionary<(string,QueryType),DnsResourceRecord[]> Records { get; } = new();
        public List<string> Queries { get; } = [];
        public string Policy { get; set; } = "";
        public string? CancelQuery {get;set;}
        public bool CancelPolicy {get;set;}
        public DnsResourceRecord[] Comparison {get;set;}=[];
        public int Guarded {get;set;}
        public override Task GuardAddressesAsync(IEnumerable<IPAddress> addresses,CancellationToken ct){Guarded+=addresses.Count();return Task.CompletedTask;}
        public override Task<IDnsQueryResponse> QueryAtAsync(string name,QueryType type,IPAddress address,CancellationToken ct)=>Task.FromResult<IDnsQueryResponse>(new Response(Comparison));
        public void Txt(string name, params string[] values) => Records[(name,QueryType.TXT)] = values.Select(v => (DnsResourceRecord)new TxtRecord(new ResourceRecordInfo(name,ResourceRecordType.TXT,QueryClass.IN,60,0),[v],[v])).ToArray();
        public override Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
        {
            if(name==CancelQuery)throw new OperationCanceledException();
            Queries.Add(name);
            return Task.FromResult<IDnsQueryResponse>(new Response(Records.GetValueOrDefault((name,type)) ?? []));
        }
        public override Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[]{IPAddress.Parse("192.0.2.2")});
        public override Task<string> FetchPolicyAsync(string host, CancellationToken ct) => CancelPolicy?throw new OperationCanceledException():Task.FromResult(Policy);
    }

    sealed class Response(DnsResourceRecord[] answers) : IDnsQueryResponse
    {
        public IReadOnlyList<DnsQuestion> Questions => [];
        public IReadOnlyList<DnsResourceRecord> Additionals => [];
        public IEnumerable<DnsResourceRecord> AllRecords => Answers;
        public IReadOnlyList<DnsResourceRecord> Answers => answers;
        public IReadOnlyList<DnsResourceRecord> Authorities => [];
        public string AuditTrail => "";
        public string ErrorMessage => "";
        public bool HasError => false;
        public DnsResponseHeader Header => new(1,0x8000,1,answers.Length,0,0);
        public int MessageSize => 0;
        public NameServer NameServer => new(IPAddress.Parse("192.0.2.53"));
        public DnsQuerySettings Settings => new DnsQueryOptions();
    }
}
