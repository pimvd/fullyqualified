using System.Net;
using DnsClient;
using DnsClient.Protocol;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class DnsCheckTests
{
    sealed class Stub : DnsCheck
    {
        public Stub() : base(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dns:CompareResolvers"] = "" }).Build()) { }
        public Dictionary<(string, QueryType), DnsResourceRecord[]> Records { get; } = new();
        public List<string> Queries { get; } = [];

        static ResourceRecordInfo Info(string name, ResourceRecordType type) => new(name, type, QueryClass.IN, 60, 0);
        public Stub Txt(string name, params string[] values) { Records[(name, QueryType.TXT)] = values.Select(v => (DnsResourceRecord)new TxtRecord(Info(name, ResourceRecordType.TXT), [v], [v])).ToArray(); return this; }
        public Stub Mx(string name, params (ushort Preference, string Exchange)[] values) { Records[(name, QueryType.MX)] = values.Select(v => (DnsResourceRecord)new MxRecord(Info(name, ResourceRecordType.MX), v.Preference, DnsString.Parse(v.Exchange))).ToArray(); return this; }
        public Stub Ns(string name) { Records[(name, QueryType.NS)] = [new NsRecord(Info(name, ResourceRecordType.NS), DnsString.Parse("ns1.example.net"))]; return this; }
        public Stub Caa(string name) { Records[(name, QueryType.CAA)] = [new CaaRecord(Info(name, ResourceRecordType.CAA), 0, "issue", "letsencrypt.org")]; return this; }

        protected override Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
        {
            Queries.Add($"{type} {name}");
            return Task.FromResult<IDnsQueryResponse>(new Response(Records.GetValueOrDefault((name, type)) ?? []));
        }
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
        public DnsResponseHeader Header => new(1, 0x8000, 1, answers.Length, 0, 0);
        public int MessageSize => 0;
        public NameServer NameServer => new(IPAddress.Parse("192.0.2.53"));
        public DnsQuerySettings Settings => new DnsQueryOptions();
    }

    static async Task<(CheckOutcome Outcome, string[] Issues)> Run(Stub dns, string name)
    {
        var outcome = await dns.RunAsync(new Asset { Name = name }, default);
        return (outcome, outcome.Values["_issues"].Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Apex_with_MX_and_reject_has_no_mail_issues()
    {
        var dns = new Stub().Ns("example.com").Caa("example.com").Mx("example.com", (10, "mail.example.com"))
            .Txt("example.com", "v=spf1 include:_spf.example.net ~all").Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var (outcome, issues) = await Run(dns, "example.com");
        Assert.Empty(issues);
        Assert.Equal(CheckStatus.Ok, outcome.Status);
        Assert.Equal("example.com", outcome.Values["dmarc.source"]);
        Assert.Equal("ns1.example.net", outcome.Values["ns"]);
        Assert.Equal("no", outcome.Values["dnssec"]);
        Assert.Equal("", outcome.Values["dkim"]); // a sender is probed for the common selectors
    }

    [Fact]
    public async Task Apex_with_sp_none_gets_the_subdomain_warning()
    {
        var dns = new Stub().Ns("example.com").Caa("example.com").Mx("example.com", (10, "mail.example.com"))
            .Txt("example.com", "v=spf1 -all include:x.example.net").Txt("_dmarc.example.com", "v=DMARC1; p=reject; sp=none");
        var (outcome, issues) = await Run(dns, "example.com");
        Assert.Equal(["DmarcSubdomainNotReject"], issues);
        Assert.Equal(CheckStatus.Warning, outcome.Status);
    }

    [Fact]
    public async Task Subdomain_with_no_records_is_a_no_mail_name_covered_by_the_apex_DMARC()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var (outcome, issues) = await Run(dns, "www.example.com");
        Assert.Equal(["NoSpf", "NoNullMx"], issues);
        Assert.Equal("example.com", outcome.Values["dmarc.source"]);
        Assert.Equal("v=DMARC1; p=reject", outcome.Values["dmarc"]);
        Assert.False(outcome.Values.ContainsKey("dkim")); // no DKIM probe, no zone values
        Assert.False(outcome.Values.ContainsKey("ns"));
        Assert.DoesNotContain(dns.Queries, q => q.StartsWith("CAA") || q.StartsWith("DS"));
        Assert.DoesNotContain(dns.Queries, q => q.Contains("_domainkey"));
    }

    [Fact]
    public async Task The_DMARC_walk_goes_up_one_label_at_a_time_and_stops_at_two_labels()
    {
        var dns = new Stub();
        var (outcome, issues) = await Run(dns, "a.b.example.com");
        Assert.Contains("NoDmarc", issues);
        Assert.Equal("", outcome.Values["dmarc.source"]);
        Assert.Equal(["TXT _dmarc.a.b.example.com", "TXT _dmarc.b.example.com", "TXT _dmarc.example.com"], dns.Queries.Where(q => q.Contains("_dmarc")));
    }

    [Fact]
    public async Task A_parent_record_applies_its_sp_to_the_name()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=reject; sp=quarantine");
        var (_, issues) = await Run(dns, "www.example.com");
        Assert.Equal(["NoSpf", "NoNullMx", "DmarcQuarantine"], issues);
    }

    [Fact]
    public async Task Sender_with_softfail_and_reject_has_no_issues()
    {
        var dns = new Stub().Mx("app.example.com", (10, "mx.example.net")).Txt("app.example.com", "V=SPF1 include:x.example.net ~all")
            .Txt("_dmarc.app.example.com", "v=DMARC1; p=reject");
        var (outcome, issues) = await Run(dns, "app.example.com");
        Assert.Empty(issues);
        Assert.Equal("app.example.com", outcome.Values["dmarc.source"]);
        Assert.Contains(dns.Queries, q => q.Contains("default._domainkey.app.example.com"));
    }

    [Fact]
    public async Task Sender_with_only_an_SPF_record_is_a_sender_without_NoNullMx()
    {
        var dns = new Stub().Txt("example.org", "v=spf1 ip4:192.0.2.1 -all").Txt("_dmarc.example.org", "v=DMARC1; p=quarantine");
        var (outcome, issues) = await Run(dns, "example.org");
        Assert.Equal(["DmarcQuarantine"], issues);
        Assert.Equal(CheckStatus.Warning, outcome.Status);
    }

    [Fact]
    public async Task Sender_with_p_none_gets_a_warning()
    {
        var dns = new Stub().Mx("app.example.com", (10, "mx.example.net")).Txt("app.example.com", "v=spf1 -all include:x.example.net").Txt("_dmarc.example.com", "v=DMARC1; p=none");
        var (_, issues) = await Run(dns, "app.example.com");
        Assert.Equal(["DmarcNone"], issues);
    }

    [Fact]
    public async Task Null_MX_with_deny_all_SPF_and_reject_has_no_issues()
    {
        var dns = new Stub().Mx("parked.example.com", (0, ".")).Txt("parked.example.com", "v=spf1  -all ").Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var (outcome, issues) = await Run(dns, "parked.example.com");
        Assert.Empty(issues);
        Assert.Equal("0 .", outcome.Values["mx"]);
        Assert.DoesNotContain(dns.Queries, q => q.Contains("_domainkey"));
    }

    [Fact]
    public async Task A_host_with_a_port_is_judged_by_its_name()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var (outcome, issues) = await Run(dns, "vpn.example.com:8443");
        Assert.Equal(["NoSpf", "NoNullMx"], issues);
        Assert.Equal("example.com", outcome.Values["dmarc.source"]);
        Assert.Contains("MX vpn.example.com", dns.Queries);
    }

    [Fact]
    public async Task Configured_DKIM_selectors_are_checked_on_a_name_that_sends_no_mail()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var outcome = await dns.RunAsync(new Asset { Name = "www.example.com", DkimSelectors = "mine" }, default);
        Assert.Contains("DkimMissing:mine", outcome.Values["_issues"]);
        Assert.Equal("missing", outcome.Values["dkim.mine"]);
    }

    [Fact]
    public async Task Only_a_dangling_CNAME_is_critical()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=none");
        Assert.Equal(CheckStatus.Warning, (await Run(dns, "www.example.com")).Outcome.Status);
    }

    [Fact]
    public async Task New_mail_values_show_up_as_changes_once_and_never_for_empty_values()
    {
        var dns = new Stub().Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var (outcome, _) = await Run(dns, "www.example.com");
        var before = new Dictionary<string, string> { ["a"] = "", ["aaaa"] = "" }; // a result from before the mail part ran on every name
        var changes = ChangeDetector.Diff(before, outcome.Values).Select(c => c.Key).ToList();
        Assert.Equal(["dmarc", "dmarc.source"], changes);
        Assert.Empty(ChangeDetector.Diff(outcome.Values, (await Run(dns, "www.example.com")).Outcome.Values));
    }

    [Theory]
    [InlineData("mail.example.com", "mail.example.com|example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("example.com", "example.com")]
    public void The_walk_has_no_public_suffix_list(string name, string expected) => Assert.Equal(expected.Split('|'), MailRules.DmarcWalk(name));

    [Fact]
    public void Discovery_applies_to_names_without_a_port_that_were_not_discovered()
    {
        var ct = new CtLogCheck(null!);
        Assert.True(ct.AppliesTo(new Asset { Name = "example.com" }));
        Assert.True(ct.AppliesTo(new Asset { Name = "www.example.com" }));
        Assert.False(ct.AppliesTo(new Asset { Name = "example.com:8443" }));
        Assert.False(ct.AppliesTo(new Asset { Name = "api.example.com", Discovered = true }));
    }
}

