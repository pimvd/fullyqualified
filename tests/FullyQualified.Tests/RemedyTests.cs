using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class RemedyTests
{
    static readonly Asset Domain = new() { Name = "example.com", Kind = AssetKind.Domain };
    static readonly Asset Endpoint = new() { Name = "shop.example.com:8443", Kind = AssetKind.Endpoint };

    static readonly ResourceManager Resources = new("FullyQualified.Web.Resources.SharedResource", typeof(SharedResource).Assembly);
    static readonly CultureInfo Dutch = new("nl");

    static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullyQualified.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "FullyQualified.Web", relative));
    }

    static IEnumerable<string> Keys(Remedy r) =>
        new[] { r.WhatKey }.Concat(r.Sections.SelectMany(s => new[] { s.StepsKey, s.HeadingKey }.Where(k => k is not null).Select(k => k!)));

    public static IEnumerable<object[]> Codes => Remedies.AllCodes.Select(c => new object[] { c });

    [Theory, MemberData(nameof(Codes))]
    public void Every_finding_has_advice_in_both_languages_with_every_placeholder_filled(string code)
    {
        var asset = code.StartsWith("Tls") ? Endpoint : Domain;
        var result = new CheckResult
        {
            AssetId = asset.Id, CheckType = code.StartsWith("Tls") ? "tls" : "dns", Status = CheckStatus.Warning, ValuesJson = "{\"cname\":\"old.provider.example\"}",
            Summary = code switch
            {
                "TlsExpired" => "Sum_TlsExpired|2026-01-01",
                "TlsExpiring" => "Sum_TlsOk|12|2026-12-01|R11",
                "TlsTrust" => "Sum_TlsChainMismatch|RemoteCertificateNameMismatch|shop.example.com|www.example.com, example.com",
                "TlsError" => "Sum_Error|connection refused",
                _ => "Sum_DnsIssues|1",
            },
        };
        var remedy = Remedies.For(code, asset, result);
        Assert.NotNull(remedy);

        foreach (var key in Keys(remedy))
            foreach (var culture in new[] { CultureInfo.InvariantCulture, Dutch })
            {
                // Looked up in that language only, so a missing Dutch text cannot hide behind the English one.
                var text = Resources.GetResourceSet(culture, true, false)?.GetString(key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{key} is missing for '{culture.Name}'");
                var filled = string.Format(text!, remedy.Args);   // throws when a placeholder has no argument
                Assert.DoesNotMatch(@"\{\d+\}", filled);
            }
        Assert.All(remedy.Sections.Where(s => s.HeadingKey is null || true), s =>
            Assert.True(Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.GetString(s.StepsKey)!.Split('\n').Length >= 3, $"{s.StepsKey} should give real steps"));
    }

    [Fact]
    public void Every_finding_the_checks_can_report_is_in_the_catalogue()
    {
        var known = Remedies.AllCodes.Select(c => c.Split(':')[0]).ToHashSet();
        // DNS findings are written in DnsCheck as issues.Add("Code") or issues.Add($"Code:...").
        var dns = Regex.Matches(Source("Checks/DnsCheck.cs"), @"issues\.Add\(\$?""([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(dns);
        Assert.All(dns, code => Assert.Contains(code, known));
        // The others come from Effective.Codes, which maps a result to the codes below.
        var produced = new[]
        {
            ("tls", CheckStatus.Critical, "Sum_TlsExpired|x"), ("tls", CheckStatus.Warning, "Sum_TlsOk|5|x|y"), ("tls", CheckStatus.Warning, "Sum_TlsChain|x"),
            ("tls", CheckStatus.Warning, "Sum_TlsChainMismatch|x"), ("tls", CheckStatus.Error, "Sum_Error|x"), ("tls", CheckStatus.Error, "Sum_Blocked"),
            ("ct", CheckStatus.Error, "Sum_Error|x"), ("dns", CheckStatus.Error, "Sum_Error|x"),
        }.SelectMany(t => Effective.Codes(new CheckResult { AssetId = Guid.NewGuid(), CheckType = t.Item1, Status = t.Item2, Summary = t.Item3 }));
        Assert.All(produced, code => Assert.Contains(code, known));
    }

    [Fact]
    public void The_advice_is_tailored_to_the_asset_with_its_own_names()
    {
        var spf = Remedies.For("NoSpf", Domain, null)!;
        Assert.Equal(["dig +short TXT example.com"], spf.Commands);
        Assert.StartsWith("v=spf1", spf.Example);
        Assert.Equal([("example.com", "TXT")], spf.Lookups);

        var dmarc = Remedies.For("NoDmarc", Domain, null)!;
        Assert.Contains("_dmarc.example.com", dmarc.Example);
        Assert.Contains("rua=mailto:dmarc@example.com", dmarc.Example);

        var dkim = Remedies.For("DkimMissing:protonmail3", Domain, null)!;
        Assert.Equal(["dig +short TXT protonmail3._domainkey.example.com"], dkim.Commands);
        Assert.Equal([("protonmail3._domainkey.example.com", "TXT")], dkim.Lookups);

        var weak = Remedies.For("DkimWeak:s1:512", Domain, null)!;
        Assert.Equal(["example.com", "s1", "512"], weak.Args.Select(a => a.ToString()));

        var mismatch = Remedies.For("ResolverMismatch:mx", Domain, null)!;
        Assert.Contains("dig +short MX example.com @1.1.1.1", mismatch.Commands);
        Assert.Equal([("example.com", "MX")], mismatch.Lookups);

        var dangling = Remedies.For("DanglingCname", Domain, new CheckResult { AssetId = Domain.Id, CheckType = "dns", Summary = "x", ValuesJson = "{\"cname\":\"gone.azurewebsites.net\"}" })!;
        Assert.Equal(["example.com", "gone.azurewebsites.net"], dangling.Args.Select(a => a.ToString()));
        Assert.Contains(("gone.azurewebsites.net", "A"), dangling.Lookups);
    }

    [Fact]
    public void A_certificate_problem_uses_the_endpoints_host_and_port_and_what_the_check_found()
    {
        var expiring = Remedies.For("TlsExpiring", Endpoint, new CheckResult { AssetId = Endpoint.Id, CheckType = "tls", Summary = "Sum_TlsOk|12|2026-12-01|R11" })!;
        Assert.Equal(["shop.example.com", "2026-12-01", "12"], expiring.Args.Select(a => a.ToString()));
        Assert.Contains("openssl s_client -connect shop.example.com:8443 -servername shop.example.com", expiring.Commands[0]);

        var both = Remedies.For("TlsTrust", Endpoint, new CheckResult { AssetId = Endpoint.Id, CheckType = "tls", Summary = "Sum_TlsChainMismatch|RemoteCertificateNameMismatch, RemoteCertificateChainErrors|shop.example.com|a.example, b.example" })!;
        Assert.Equal(2, both.Sections.Count);                         // name mismatch and chain problem: both explained, each under its own heading
        Assert.All(both.Sections, s => Assert.NotNull(s.HeadingKey));
        Assert.Equal("a.example, b.example", both.Args[1]);

        var chainOnly = Remedies.For("TlsTrust", Endpoint, new CheckResult { AssetId = Endpoint.Id, CheckType = "tls", Summary = "Sum_TlsChain|RemoteCertificateChainErrors" })!;
        Assert.Single(chainOnly.Sections);
        Assert.Null(chainOnly.Sections[0].HeadingKey);                // one situation needs no heading
        Assert.Equal("Fix_TlsTrust_Chain_Steps", chainOnly.Sections[0].StepsKey);
    }

    [Fact]
    public void Unknown_findings_have_no_advice_and_links_point_at_the_fix_page()
    {
        Assert.Null(Remedies.For("SomethingNew", Domain, null));
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal($"https://fq.example.org/fix?asset={id}&check=dns&code=DkimMissing%3As1", Remedies.Link("https://fq.example.org/", id, "dns", "DkimMissing:s1"));
    }
}

public class FixPageTests : IClassFixture<PgFactory>
{
    readonly PgFactory factory;
    public FixPageTests(PgFactory factory) => this.factory = factory;


    [DbFact]
    public async Task The_fix_page_gives_tailored_steps_commands_and_lookups_and_is_closed_to_other_tenants()
    {
        var a = new Probe(factory);
        var (mine, theirs) = await a.Seed();
        var client = factory.As(mine.Admin);

        var page = System.Net.WebUtility.HtmlDecode(await (await client.GetAsync($"/fix?asset={mine.AssetId}&check=dns&code=DkimMissing%3As1")).Content.ReadAsStringAsync());
        Assert.Contains("What this means", page);
        Assert.Contains("What to do", page);
        Assert.Contains($"s1._domainkey.{mine.Name}", page);                                   // the real name, in text, command and lookup
        Assert.Contains($"dig +short TXT s1._domainkey.{mine.Name}", page);
        Assert.Contains($"data-dig-name=\"s1._domainkey.{mine.Name}\" data-dig-as=\"TXT\"", page);

        var spf = System.Net.WebUtility.HtmlDecode(await (await client.GetAsync($"/fix?asset={mine.AssetId}&check=dns&code=NoSpf")).Content.ReadAsStringAsync());
        Assert.Contains("v=spf1", spf);

        var mismatch = await (await client.GetAsync($"/fix?asset={mine.TlsAssetId}&check=tls&code=TlsTrust")).Content.ReadAsStringAsync();
        Assert.Contains("a.example, b.example", mismatch);                                      // what the certificate covers, from the stored result
        Assert.Contains("is not among those names", mismatch);                                  // one situation, so no heading, just its steps

        Assert.Contains("There is no advice", await (await client.GetAsync($"/fix?asset={mine.AssetId}&check=dns&code=Nonsense")).Content.ReadAsStringAsync());
        Assert.Contains("Not found", await (await client.GetAsync($"/fix?asset={theirs}&check=dns&code=NoSpf")).Content.ReadAsStringAsync()); // another tenant's asset

        // The asset page and the dashboard link to it.
        Assert.Contains($"/fix?asset={mine.AssetId}", await (await client.GetAsync($"/assets/{mine.AssetId}")).Content.ReadAsStringAsync());
        Assert.Contains("class=\"fix-link\"", await (await client.GetAsync("/")).Content.ReadAsStringAsync());
    }

    sealed class Probe(PgFactory factory) : OrgTestBase<PgFactory>(factory)
    {
        public record Mine(Guid Admin, Guid AssetId, Guid TlsAssetId, string Name);

        public async Task<(Mine, Guid)> Seed()
        {
            var org = await NewOrg();
            var other = await NewOrg();
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var domain = new Asset { TenantId = org.Id, Name = $"fix.{org.Id:N}.test", Kind = AssetKind.Domain };
            var endpoint = new Asset { TenantId = org.Id, Name = $"web.{org.Id:N}.test", Kind = AssetKind.Endpoint };
            var foreign = new Asset { TenantId = other.Id, Name = $"foreign.{other.Id:N}.test", Kind = AssetKind.Domain };
            db.Assets.AddRange(domain, endpoint, foreign);
            db.Results.AddRange(
                new CheckResult { TenantId = org.Id, AssetId = domain.Id, CheckType = "dns", Status = CheckStatus.Warning, Summary = "Sum_DnsIssues|2", ValuesJson = "{\"_issues\":\"NoSpf,DkimMissing:s1\"}" },
                new CheckResult { TenantId = org.Id, AssetId = endpoint.Id, CheckType = "tls", Status = CheckStatus.Warning, Summary = "Sum_TlsChainMismatch|RemoteCertificateNameMismatch|x|a.example, b.example" });
            await db.SaveChangesAsync();
            return (new Mine(org.Admin, domain.Id, endpoint.Id, domain.Name), foreign.Id);
        }
    }
}
