using System.Security.Cryptography;
using FullyQualified.Web;

namespace FullyQualified.Tests;

public class DnsHelperTests
{
    static string Spki(int bits)
    {
        using var rsa = RSA.Create(bits);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Selectors_are_split_normalized_deduplicated_and_validated()
    {
        Assert.Equal(["selector1", "selector2", "k1"], Dkim.ParseSelectors("Selector1, selector2;k1 SELECTOR1"));
        Assert.Empty(Dkim.ParseSelectors("  ")!);
        Assert.Null(Dkim.ParseSelectors("bad selector!"));
        Assert.Null(Dkim.ParseSelectors("-x"));
        Assert.Null(Dkim.ParseSelectors(string.Join(",", Enumerable.Range(0, 11).Select(i => "s" + i))));
    }

    [Fact]
    public void A_valid_record_reports_key_size_and_a_stable_fingerprint()
    {
        var key = Spki(2048);
        var a = Dkim.Analyze([$"v=DKIM1; k=rsa; p={key}"]);
        Assert.Equal(Dkim.State.Ok, a.State);
        Assert.Equal(2048, a.KeyBits);
        Assert.Equal(a.Fingerprint, Dkim.Analyze([$"v=DKIM1; p={key[..40]} {key[40..]}"]).Fingerprint); // folded TXT strings
        Assert.NotEqual(a.Fingerprint, Dkim.Analyze([$"v=DKIM1; p={Spki(2048)}"]).Fingerprint);          // a rotated key changes it
    }

    [Fact]
    public void Weak_keys_report_their_size() => Assert.Equal(1024, Dkim.Analyze([$"v=DKIM1; p={Spki(1024)}"]).KeyBits);

    [Fact]
    public void Missing_revoked_and_invalid_records_are_told_apart()
    {
        Assert.Equal(Dkim.State.Missing, Dkim.Analyze(["v=spf1 -all"]).State);
        Assert.Equal(Dkim.State.Missing, Dkim.Analyze([]).State);
        Assert.Equal(Dkim.State.Revoked, Dkim.Analyze(["v=DKIM1; k=rsa; p="]).State);
        Assert.Equal(Dkim.State.Invalid, Dkim.Analyze(["v=DKIM1; p=not-base64!!"]).State);
        Assert.Equal(Dkim.State.Invalid, Dkim.Analyze(["v=DKIM1; p=AAAA"]).State);
    }

    [Fact]
    public void Resolver_differences_name_the_record_types_that_disagree()
    {
        var primary = new Dictionary<string, string> { ["ns"] = "a; b", ["mx"] = "10 m", ["caa"] = "" };
        IReadOnlyDictionary<string, string> same = new Dictionary<string, string> { ["ns"] = "a; b", ["mx"] = "10 m", ["caa"] = "" };
        IReadOnlyDictionary<string, string> hijacked = new Dictionary<string, string> { ["ns"] = "evil", ["mx"] = "10 m", ["caa"] = "" };
        Assert.Empty(ResolverCompare.Differences(primary, [same, same]));
        Assert.Equal(["ns"], ResolverCompare.Differences(primary, [same, hijacked]));
        Assert.Empty(ResolverCompare.Differences(primary, []));
    }

    [Fact]
    public void Issues_with_arguments_fill_the_placeholders()
    {
        var l = new KeyLocalizer();
        var result = new CheckResult { AssetId = Guid.NewGuid(), CheckType = "dns", Summary = "Sum_DnsIssues|2", ValuesJson = "{\"_issues\":\"DkimMissing:s1,DkimWeak:s2:512\"}" };
        Assert.Equal(["Issue_DkimMissing", "Issue_DkimWeak"], ResourceText.Issues(l, result));
    }
}
