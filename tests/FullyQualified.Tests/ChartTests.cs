using System.Text.RegularExpressions;
using FullyQualified.Web;

namespace FullyQualified.Tests;

public class ChartTests
{
    static readonly ChartSeries[] Series = [new("ok", "OK", "s-ok"), new("warning", "Warning", "s-warning")];

    [Theory]
    [InlineData(0, 4, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(9, 10, 5)]
    [InlineData(37, 40, 10)]
    [InlineData(100, 100, 50)]
    [InlineData(1234, 1500, 500)]
    public void The_axis_rounds_up_to_a_clean_maximum(int max, int expectedMax, int expectedStep)
    {
        var (m, step) = Charts.NiceScale(max);
        Assert.Equal((expectedMax, expectedStep), (m, step));
        Assert.True(m >= max);
        Assert.True(m / step <= 6);
    }

    [Fact]
    public void Each_column_has_a_tooltip_and_a_hit_area_and_empty_days_draw_no_marks()
    {
        var cols = new List<ChartColumn> { new("1 Oct", "2026-10-01: OK 3 · Warning 1", [3, 1]), new("2 Oct", "2026-10-02: OK 0 · Warning 0", [0, 0]) };
        var svg = Charts.StackedColumns(cols, Series, "Results per day");
        Assert.Equal(2, Regex.Matches(svg, "<g class=\"col\"").Count);
        Assert.Equal(2, Regex.Matches(svg, "<title>").Count);
        Assert.Equal(2, Regex.Matches(svg, "class=\"hit\"").Count);
        Assert.Equal(2, Regex.Matches(svg, "class=\"seg ").Count);     // only the first day has segments
        Assert.Contains("aria-label=\"Results per day\"", svg);
        Assert.Contains("2026-10-01: OK 3", svg);
    }

    [Fact]
    public void Columns_are_thin_and_stacked_segments_are_separated_by_a_surface_gap()
    {
        var cols = Enumerable.Range(1, 30).Select(i => new ChartColumn($"{i}", $"day {i}", [10, 5])).ToList();
        var svg = Charts.StackedColumns(cols, Series, "x");
        var widths = Regex.Matches(svg, "<rect class=\"seg [^\"]*\" x=\"[^\"]*\" y=\"[^\"]*\" width=\"([\\d.]+)\"").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(widths, w => Assert.InRange(w, 1, 24));

        var one = Charts.StackedColumns([new("a", "a", [10, 10])], Series, "x");
        var bottom = Regex.Match(one, "<rect class=\"seg s-ok\" x=\"[\\d.]+\" y=\"([\\d.]+)\" width=\"[\\d.]+\" height=\"([\\d.]+)\"");
        var top = Regex.Match(one, "<path class=\"seg s-warning\" d=\"M[\\d.]+,([\\d.]+)a4,4 0 0 1 4,-4h[\\d.]+a4,4 0 0 1 4,4v([\\d.]+)h-[\\d.]+z\"");
        Assert.True(top.Success, "the top segment has 4px rounded data end");
        var bottomTop = double.Parse(bottom.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var topBottom = double.Parse(top.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + double.Parse(top.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(2, Math.Round(bottomTop - topBottom, 1)); // 2px of surface between the two fills
    }

    [Fact]
    public void The_axis_shows_compact_ticks_and_never_a_label_on_every_column()
    {
        var cols = Enumerable.Range(1, 90).Select(i => new ChartColumn($"d{i}", $"day {i}", [i * 20, 0])).ToList();
        var svg = Charts.StackedColumns(cols, Series, "x");
        Assert.Matches(@">\d(\.\d)?K<", svg);
        Assert.InRange(Regex.Matches(svg, ">d\\d+<").Count, 5, 15);
    }

    [Fact]
    public void Horizontal_bars_put_the_total_at_the_tip_and_scale_to_the_largest_row()
    {
        var rows = new List<(string, string, int[])> { ("TLS", "TLS: OK 9", [9, 0]), ("DNS", "DNS: OK 3 · Warning 1", [3, 1]) };
        var svg = Charts.StackedBars(rows, Series, "Per check");
        Assert.Equal(2, Regex.Matches(svg, "<g class=\"col\"").Count);
        Assert.Contains(">9<", svg);
        Assert.Contains(">4<", svg);
        var tls = double.Parse(Regex.Match(svg, "<path class=\"seg s-ok\" d=\"M150,[\\d.]+h([\\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 4;
        Assert.InRange(tls, 540, 560); // the largest row spans the full plot width
    }

    [Fact]
    public void Text_in_tooltips_is_html_encoded()
    {
        var svg = Charts.StackedColumns([new("<b>", "<script>alert(1)</script>", [1, 0])], Series, "a\"b");
        Assert.DoesNotContain("<script>", svg);
        Assert.DoesNotContain("<b>", svg);
        Assert.Contains("a&quot;b", svg);
    }

    [Theory]
    [InlineData(CheckStatus.Warning, CheckStatus.Ok, 0, true)]    // got worse
    [InlineData(CheckStatus.Warning, null, 0, true)]               // new and bad
    [InlineData(CheckStatus.Ok, null, 0, false)]                   // new and fine
    [InlineData(CheckStatus.Warning, CheckStatus.Error, 0, false)] // got better but still bad: not worse
    [InlineData(CheckStatus.Ok, CheckStatus.Warning, 0, true)]     // recovered
    [InlineData(CheckStatus.Ok, CheckStatus.Ok, 2, true)]          // drift
    public void Policy_alarms_on_worse_recovered_and_drift(CheckStatus now, CheckStatus? before, int changes, bool expected) =>
        Assert.Equal(expected, AlertPolicy.ShouldAlert(new AlertConfig(true, "https://x.test/h", null, null, CheckStatus.Warning, true, true), before, now, changes));

    [Fact]
    public void Policy_respects_the_minimum_status_toggles_and_missing_channels()
    {
        var baseConfig = new AlertConfig(true, "https://x.test/h", null, null, CheckStatus.Error, false, false);
        Assert.False(AlertPolicy.ShouldAlert(baseConfig, CheckStatus.Ok, CheckStatus.Warning, 0));   // below the minimum
        Assert.True(AlertPolicy.ShouldAlert(baseConfig, CheckStatus.Ok, CheckStatus.Error, 0));
        Assert.False(AlertPolicy.ShouldAlert(baseConfig, CheckStatus.Error, CheckStatus.Ok, 0));     // recovery off
        Assert.False(AlertPolicy.ShouldAlert(baseConfig, CheckStatus.Ok, CheckStatus.Ok, 3));        // drift off
        Assert.False(AlertPolicy.ShouldAlert(baseConfig with { Enabled = false }, CheckStatus.Ok, CheckStatus.Critical, 0));
        Assert.False(AlertPolicy.ShouldAlert(baseConfig with { SlackWebhook = null }, CheckStatus.Ok, CheckStatus.Critical, 0)); // nowhere to send
    }
}
