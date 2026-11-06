using FullyQualified.Web;
using Xunit;

namespace FullyQualified.Tests;

public class AlertRuleTests
{
    static readonly Guid Tenant = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid();
    static AlertRule Rule(string type, string key, Guid? asset = null) => new() { TenantId = Tenant, CheckType = type, Key = key, AssetId = asset };
    static List<(string Key, string Before, string After)> Diff(params string[] keys) => keys.Select(k => (k, "old", "new")).ToList();

    [Theory]
    [InlineData("dns", "ns", true)]
    [InlineData("dns", "mx", true)]
    [InlineData("dns", "a", true)]
    [InlineData("dns", "dkim", true)]
    [InlineData("tls", "issuer", true)]
    [InlineData("dns", "*", true)]            // any change of a check
    [InlineData("tls", "*", true)]
    [InlineData("dns", "issuer", false)]      // a key of another check
    [InlineData("dns", "_issues", false)]     // internal values are never changes
    [InlineData("dns", "nonsense", false)]
    [InlineData("ct", "*", false)]            // the certificate-log search never alarms
    [InlineData("nope", "*", false)]
    public void Only_values_a_check_really_has_can_be_alarmed_on(string type, string key, bool known) =>
        Assert.Equal(known, AlertRules.IsKnown(type, key));

    [Fact]
    public void Every_catalog_entry_is_known_and_has_a_label()
    {
        Assert.NotEmpty(AlertRules.Catalog);
        Assert.All(AlertRules.Catalog, c => Assert.True(AlertRules.IsKnown(c.CheckType, c.Key)));
    }

    [Fact]
    public void A_rule_matches_the_change_of_its_value_on_every_asset()
    {
        var rules = new[] { Rule("dns", "ns") };
        Assert.Single(AlertRules.Hits(rules, A, "dns", Diff("ns", "mx")));
        Assert.Single(AlertRules.Hits(rules, B, "dns", Diff("ns")));
        Assert.Empty(AlertRules.Hits(rules, A, "dns", Diff("mx", "spf")));
        Assert.Empty(AlertRules.Hits(rules, A, "tls", Diff("ns")));          // same key name, other check
    }

    [Fact]
    public void A_rule_for_one_asset_ignores_the_others()
    {
        var rules = new[] { Rule("dns", "mx", A) };
        Assert.Single(AlertRules.Hits(rules, A, "dns", Diff("mx")));
        Assert.Empty(AlertRules.Hits(rules, B, "dns", Diff("mx")));
    }

    [Fact]
    public void Any_change_matches_every_value_of_the_check_and_dkim_covers_every_selector()
    {
        Assert.Equal(3, AlertRules.Hits([Rule("dns", "*")], A, "dns", Diff("ns", "mx", "dkim.s1")).Count);
        Assert.Single(AlertRules.Hits([Rule("dns", "dkim")], A, "dns", Diff("dkim.google", "spf")));
        Assert.Empty(AlertRules.Hits([Rule("dns", "dkim")], A, "dns", Diff("dkimx")));
    }

    [Fact]
    public void Several_rules_matching_one_change_report_it_once()
    {
        var hits = AlertRules.Hits([Rule("dns", "ns"), Rule("dns", "*"), Rule("dns", "ns", A)], A, "dns", Diff("ns"));
        Assert.Single(hits);
    }
}
