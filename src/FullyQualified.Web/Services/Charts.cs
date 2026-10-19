using System.Globalization;
using System.Net;
using System.Text;

namespace FullyQualified.Web;

/// <param name="Css">Class that fills the mark, defined in app.css so light and dark share one place.</param>
public record ChartSeries(string Key, string Label, string Css);

/// <param name="Label">Short x-axis text.</param>
/// <param name="Tip">Full text for the tooltip and screen readers.</param>
public record ChartColumn(string Label, string Tip, int[] Values);

/// <summary>
/// Server-rendered SVG charts: no script, so they work in the same locked-down setting as the rest of the app.
/// Marks follow the house rules: columns at most 24px wide, 4px rounded data end, a 2px surface gap between stacked segments,
/// hairline grid, one axis, and a native tooltip (&lt;title&gt;) on a hit area larger than the mark.
/// </summary>
public static class Charts
{
    const double Width = 760, Height = 250, Left = 38, Right = 8, Top = 10, Bottom = 26, MaxBar = 24, Gap = 2, Radius = 4;

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Rounds a maximum up to a multiple of a clean step (1, 2, 5 or 10 times a power of ten), giving about four steps.</summary>
    public static (int Max, int Step) NiceScale(int max)
    {
        if (max <= 0) return (4, 1);
        var raw = max / 4.0; // aim for about four steps
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        step = Math.Max(1, Math.Ceiling(step));
        return ((int)(Math.Ceiling(max / step) * step), (int)step);
    }

    static string Compact(int v) => v >= 1_000_000 ? $"{v / 1_000_000.0:0.#}M" : v >= 1000 ? $"{v / 1000.0:0.#}K" : v.ToString(CultureInfo.InvariantCulture);

    public static string StackedColumns(IReadOnlyList<ChartColumn> cols, IReadOnlyList<ChartSeries> series, string description)
    {
        var (yMax, step) = NiceScale(cols.Select(c => c.Values.Sum()).DefaultIfEmpty(0).Max());
        double plotW = Width - Left - Right, plotH = Height - Top - Bottom, baseline = Top + plotH;
        var slot = cols.Count == 0 ? plotW : plotW / cols.Count;
        var barW = Math.Min(MaxBar, slot * 0.7);
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"chart\" viewBox=\"0 0 {F(Width)} {F(Height)}\" role=\"img\" aria-label=\"{E(description)}\" preserveAspectRatio=\"xMidYMid meet\">");

        for (var t = 0; t <= yMax; t += step)
        {
            var y = baseline - t / (double)yMax * plotH;
            sb.Append($"<line class=\"{(t == 0 ? "axis" : "grid")}\" x1=\"{F(Left)}\" x2=\"{F(Width - Right)}\" y1=\"{F(y)}\" y2=\"{F(y)}\"/>");
            sb.Append($"<text x=\"{F(Left - 6)}\" y=\"{F(y + 4)}\" text-anchor=\"end\">{Compact(t)}</text>");
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(cols.Count / 8.0));
        for (var i = 0; i < cols.Count; i++)
        {
            var x0 = Left + i * slot;
            var cx = x0 + slot / 2;
            sb.Append($"<g class=\"col\" tabindex=\"0\" role=\"img\" aria-label=\"{E(cols[i].Tip)}\"><title>{E(cols[i].Tip)}</title>");
            sb.Append($"<rect class=\"hit\" x=\"{F(x0)}\" y=\"{F(Top)}\" width=\"{F(slot)}\" height=\"{F(plotH)}\"/>");

            var topIndex = Array.FindLastIndex(cols[i].Values, v => v > 0);
            double cum = 0;
            for (var s = 0; s < series.Count && s < cols[i].Values.Length; s++)
            {
                var v = cols[i].Values[s];
                if (v <= 0) continue;
                var h = v / (double)yMax * plotH;
                var drawn = cum > 0 ? Math.Max(h - Gap, 0.5) : h; // the surface gap sits below every segment but the first
                var y = baseline - cum - h;
                var x = cx - barW / 2;
                sb.Append(s == topIndex && drawn >= Radius * 2
                    ? $"<path class=\"seg {series[s].Css}\" d=\"M{F(x)},{F(y + Radius)}a{F(Radius)},{F(Radius)} 0 0 1 {F(Radius)},{F(-Radius)}h{F(barW - 2 * Radius)}a{F(Radius)},{F(Radius)} 0 0 1 {F(Radius)},{F(Radius)}v{F(drawn - Radius)}h{F(-barW)}z\"/>"
                    : $"<rect class=\"seg {series[s].Css}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(barW)}\" height=\"{F(drawn)}\"/>");
                cum += h;
            }
            sb.Append("</g>");
            if (i % labelEvery == 0 || i == cols.Count - 1 && labelEvery == 1)
                sb.Append($"<text x=\"{F(cx)}\" y=\"{F(Height - 8)}\" text-anchor=\"middle\">{E(cols[i].Label)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>One horizontal stacked bar per row with its total at the tip. Rows must be few; past about seven a table is the better form.</summary>
    public static string StackedBars(IReadOnlyList<(string Label, string Tip, int[] Values)> rows, IReadOnlyList<ChartSeries> series, string description)
    {
        const double labelW = 150, rowH = 30, barH = 16;
        var max = rows.Select(r => r.Values.Sum()).DefaultIfEmpty(0).Max();
        var height = Math.Max(rows.Count, 1) * rowH + 8;
        double plotW = Width - labelW - 56;
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"chart\" viewBox=\"0 0 {F(Width)} {F(height)}\" role=\"img\" aria-label=\"{E(description)}\" preserveAspectRatio=\"xMidYMid meet\">");
        sb.Append($"<line class=\"axis\" x1=\"{F(labelW)}\" x2=\"{F(labelW)}\" y1=\"2\" y2=\"{F(height - 2)}\"/>");
        for (var i = 0; i < rows.Count; i++)
        {
            var y = 4 + i * rowH + (rowH - barH) / 2;
            sb.Append($"<g class=\"col\" tabindex=\"0\" role=\"img\" aria-label=\"{E(rows[i].Tip)}\"><title>{E(rows[i].Tip)}</title>");
            sb.Append($"<rect class=\"hit\" x=\"0\" y=\"{F(4 + i * rowH)}\" width=\"{F(Width)}\" height=\"{F(rowH)}\"/>");
            sb.Append($"<text x=\"{F(labelW - 10)}\" y=\"{F(y + barH / 2 + 4)}\" text-anchor=\"end\">{E(rows[i].Label)}</text>");
            double x = labelW;
            var total = rows[i].Values.Sum();
            var last = Array.FindLastIndex(rows[i].Values, v => v > 0);
            for (var s = 0; s < series.Count && s < rows[i].Values.Length; s++)
            {
                var v = rows[i].Values[s];
                if (v <= 0 || max == 0) continue;
                var w = v / (double)max * plotW;
                var drawn = x > labelW ? Math.Max(w - Gap, 0.5) : w; // gap on the left of every segment but the first
                var x0 = x > labelW ? x + Gap : x;
                var shown = x > labelW ? w - Gap : w;
                sb.Append(s == last && shown >= Radius * 2
                    ? $"<path class=\"seg {series[s].Css}\" d=\"M{F(x0)},{F(y)}h{F(shown - Radius)}a{F(Radius)},{F(Radius)} 0 0 1 {F(Radius)},{F(Radius)}v{F(barH - 2 * Radius)}a{F(Radius)},{F(Radius)} 0 0 1 {F(-Radius)},{F(Radius)}h{F(-(shown - Radius))}z\"/>"
                    : $"<rect class=\"seg {series[s].Css}\" x=\"{F(x0)}\" y=\"{F(y)}\" width=\"{F(Math.Max(shown, 0.5))}\" height=\"{F(barH)}\"/>");
                x += w;
                _ = drawn;
            }
            sb.Append($"<text x=\"{F(x + 8)}\" y=\"{F(y + barH / 2 + 4)}\">{total.ToString("N0", CultureInfo.CurrentCulture)}</text></g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
