using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace FullyQualified.Web;

public record DomainControl(string Domain, bool Spf, bool Dmarc, bool Caa, bool Dnssec);
public record CertRow(string Host, string Issuer, DateTimeOffset NotAfter, int DaysLeft, string Source, string Renewal);
public record ChangeRow(DateTimeOffset At, string Asset, string Check, string Key, string Before, string After);
public record ReviewRow(string Host, string Domain, string Service, ReviewState State, string? Reason, DateTimeOffset? DecidedAt, string? DecidedBy);
public record ReportModel(DateTimeOffset GeneratedAt, int Assets, int Certificates, int ExpiringSoon, int Expired,
    List<DomainControl> Domains, List<CertRow> Certs, List<ChangeRow> Changes, List<ReviewRow>? Review = null)
{
    public List<ReviewRow> ReviewRows => Review ?? [];
}

public class ReportBuilder(AppDbContext db)
{
    public async Task<ReportModel> BuildAsync()
    {
        var assets = await db.Assets.ToListAsync();
        var names = assets.ToDictionary(a => a.Id, a => a.Name);
        var latest = await db.LatestResultsAsync();
        var certs = await db.Certificates.OrderBy(c => c.NotAfter).ToListAsync();
        var parents = assets.ToDictionary(a => a.Id, a => a.Name);
        var review = (await db.ReviewHosts.OrderBy(h => h.State).ThenBy(h => h.Name).ToListAsync())
            .Select(h => new ReviewRow(h.Name, h.ParentAssetId is { } p && parents.TryGetValue(p, out var d) ? d : "", h.Service ?? "", h.State, h.Reason, h.DecidedAt, h.DecidedBy)).ToList();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var changes = await db.Changes.Where(c => c.DetectedAt >= since).OrderByDescending(c => c.DetectedAt).Take(500).ToListAsync();

        // Only a zone apex has NS records, so the zone values in the latest DNS result pick out the domains.
        var domains = assets.OrderBy(a => a.Name).Select(a => (a, v: latest.FirstOrDefault(r => r.AssetId == a.Id && r.CheckType == "dns")?.Values ?? []))
            .Where(x => Has(x.v, "ns"))
            .Select(x => new DomainControl(x.a.Name, Has(x.v, "spf"), Has(x.v, "dmarc"), Has(x.v, "caa"), x.v.GetValueOrDefault("dnssec") == "yes")).ToList();

        // CT lists the same certificate under several hosts; the inventory counts distinct certificates.
        var rows = certs.Select(c => new CertRow(c.Host, c.Issuer, c.NotAfter, c.DaysLeft, c.Source, c.Renewal)).ToList();
        // A muted certificate is still listed, but it does not count as expiring or expired.
        var now = DateTimeOffset.UtcNow;
        var counted = certs.Where(c => !c.IsMuted(now)).ToList();
        return new(now, assets.Count, certs.Count, counted.Count(c => c.DaysLeft is >= 0 and <= 30),
            counted.Count(c => c.DaysLeft < 0), domains, rows,
            changes.Select(c => new ChangeRow(c.DetectedAt, names.GetValueOrDefault(c.AssetId, "?"), c.CheckType, c.Key, c.Before, c.After)).ToList(), review);
    }

    static bool Has(Dictionary<string, string> v, string key) => !string.IsNullOrEmpty(v.GetValueOrDefault(key));
}

public static class ReportWriter
{
    static string YesNo(IStringLocalizer l, bool b) => l[b ? "Rep_Yes" : "Rep_No"];

    static string ReasonText(IStringLocalizer l, ReviewRow r) => r.Reason == ReviewHost.RemovedReason ? l["Review_RemovedReason"].Value : r.Reason ?? "";
    static string DecidedBy(IStringLocalizer l, ReviewRow r) => r.DecidedBy ?? (r.State == ReviewState.Added ? l["Review_Automatic"].Value : "");
    static string ReviewSummary(IStringLocalizer l, ReportModel m) =>
        l["Rep_ReviewSummary", m.ReviewRows.Count(r => r.State == ReviewState.Added), m.ReviewRows.Count(r => r.State == ReviewState.Ignored), m.ReviewRows.Count(r => r.State == ReviewState.Pending)].Value;

    public static byte[] Excel(ReportModel m, IStringLocalizer l)
    {
        using var wb = new XLWorkbook();
        var s = wb.AddWorksheet(Trunc(l["Rep_Summary"]));
        s.Cell(1, 1).Value = l["Rep_Title"].Value; s.Cell(1, 1).Style.Font.Bold = true;
        (string, object)[] summary =
        [
            (l["Rep_GeneratedAt"], m.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")), (l["Rep_Assets"], m.Assets),
            (l["Rep_Certs"], m.Certificates), (l["Rep_Expiring"], m.ExpiringSoon), (l["Rep_Expired"], m.Expired),
            (l["Rep_ReviewTitle"], ReviewSummary(l, m)),
        ];
        for (var i = 0; i < summary.Length; i++)
        {
            s.Cell(i + 3, 1).Value = summary[i].Item1;
            s.Cell(i + 3, 2).Value = XLCellValue.FromObject(summary[i].Item2);
        }
        s.Columns().AdjustToContents();

        Sheet(wb, l["Nav_Certificates"], [l["Col_Name"], l["Col_Issuer"], l["Col_Expires"], l["Col_Days"], l["Col_Source"], l["Col_Renewal"]],
            m.Certs.Select(c => new object[] { c.Host, c.Issuer, c.NotAfter.ToString("yyyy-MM-dd"), c.DaysLeft, l["Source_" + c.Source].Value, l["Renewal_" + c.Renewal].Value }));
        Sheet(wb, l["Rep_DnsControls"], [l["Col_Domain"], l["Ctl_Spf"], l["Ctl_Dmarc"], l["Ctl_Caa"], l["Ctl_Dnssec"]],
            m.Domains.Select(d => new object[] { d.Domain, YesNo(l, d.Spf), YesNo(l, d.Dmarc), YesNo(l, d.Caa), YesNo(l, d.Dnssec) }));
        Sheet(wb, l["Rep_ReviewTitle"], [l["Col_Name"], l["Col_Domain"], l["Col_FoundVia"], l["Col_Status"], l["Col_Reason"], l["Col_Decided"], l["Col_DecidedBy"]],
            m.ReviewRows.Select(r => new object[] { r.Host, r.Domain, r.Service, l["Review_State_" + r.State].Value, ReasonText(l, r), r.DecidedAt?.ToString("yyyy-MM-dd") ?? "", DecidedBy(l, r) }));
        Sheet(wb, l["Rep_ChangesTitle"], [l["Col_Detected"], l["Col_Asset"], l["Col_Check"], l["Col_Change"], l["Col_Before"], l["Col_After"]],
            m.Changes.Select(c => new object[] { c.At.ToString("yyyy-MM-dd HH:mm"), c.Asset, l["Check_" + c.Check].Value, c.Key, c.Before, c.After }));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    static string Trunc(string name) => name.Length > 31 ? name[..31] : name;

    static void Sheet(XLWorkbook wb, string name, string[] headers, IEnumerable<object[]> rows)
    {
        var ws = wb.AddWorksheet(Trunc(name));
        for (var c = 0; c < headers.Length; c++) { ws.Cell(1, c + 1).Value = headers[c]; ws.Cell(1, c + 1).Style.Font.Bold = true; }
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++) ws.Cell(r, c + 1).Value = XLCellValue.FromObject(row[c]);
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    public static byte[] Pdf(ReportModel m, IStringLocalizer l)
    {
        PdfFonts.Register();
        var doc = new Document();
        doc.Styles[StyleNames.Normal]!.Font.Name = PdfFonts.Family;
        doc.Styles[StyleNames.Normal]!.Font.Size = 9;

        var sec = doc.AddSection();
        // PageWidth is only filled in at render time, so set A4 explicitly to size the tables.
        sec.PageSetup.PageWidth = Unit.FromCentimeter(21);
        sec.PageSetup.PageHeight = Unit.FromCentimeter(29.7);
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Unit.FromPoint(32);
        sec.PageSetup.TopMargin = Unit.FromPoint(80);
        sec.PageSetup.BottomMargin = Unit.FromPoint(40);
        sec.PageSetup.HeaderDistance = Unit.FromPoint(28);
        var width = sec.PageSetup.PageWidth.Point - sec.PageSetup.LeftMargin.Point - sec.PageSetup.RightMargin.Point;

        var title = sec.Headers.Primary.AddParagraph(l["Rep_Title"].Value);
        title.Format.Font.Size = 20; title.Format.Font.Bold = true;
        var stamp = sec.Headers.Primary.AddParagraph($"{l["Rep_GeneratedAt"]}: {m.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        stamp.Format.Font.Color = Colors.Gray; stamp.Format.SpaceAfter = 8;

        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddPageField(); footer.AddText(" / "); footer.AddNumPagesField();

        Heading(sec, l["Rep_Summary"].Value);
        sec.AddParagraph($"{l["Rep_Assets"]}: {m.Assets}   {l["Rep_Certs"]}: {m.Certificates}   {l["Rep_Expiring"]}: {m.ExpiringSoon}   {l["Rep_Expired"]}: {m.Expired}");

        Heading(sec, l["Rep_DnsControls"].Value);
        Table(sec, width, [l["Col_Domain"], l["Ctl_Spf"], l["Ctl_Dmarc"], l["Ctl_Caa"], l["Ctl_Dnssec"]],
            m.Domains.Select(d => new[] { d.Domain, YesNo(l, d.Spf), YesNo(l, d.Dmarc), YesNo(l, d.Caa), YesNo(l, d.Dnssec) }), [3, 1, 1, 1, 1]);

        Heading(sec, l["Nav_Certificates"].Value);
        Table(sec, width, [l["Col_Name"], l["Col_Issuer"], l["Col_Expires"], l["Col_Days"], l["Col_Renewal"]],
            m.Certs.Select(c => new[] { c.Host, c.Issuer, c.NotAfter.ToString("yyyy-MM-dd"), c.DaysLeft.ToString(), l["Renewal_" + c.Renewal].Value }), [3, 2, 1.4f, 1, 1.6f]);

        Heading(sec, l["Rep_ReviewTitle"].Value);
        sec.AddParagraph(ReviewSummary(l, m));
        Table(sec, width, [l["Col_Name"], l["Col_Domain"], l["Col_Status"], l["Col_Reason"], l["Col_Decided"]],
            m.ReviewRows.Select(r => new[] { r.Host, r.Domain, l["Review_State_" + r.State].Value, Cut(ReasonText(l, r)), $"{r.DecidedAt:yyyy-MM-dd} {DecidedBy(l, r)}".Trim() }), [2.6f, 2, 1.1f, 2.6f, 2]);

        Heading(sec, l["Rep_ChangesTitle"].Value);
        Table(sec, width, [l["Col_Detected"], l["Col_Asset"], l["Col_Change"], l["Col_Before"], l["Col_After"]],
            m.Changes.Take(100).Select(c => new[] { c.At.ToString("yyyy-MM-dd"), c.Asset, c.Key, Cut(c.Before), Cut(c.After) }), [1.4f, 2, 1.5f, 2.3f, 2.3f]);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms);
        return ms.ToArray();
    }

    static string Cut(string s) => s.Length > 60 ? s[..60] + "…" : s;

    static void Heading(Section sec, string text)
    {
        var p = sec.AddParagraph(text);
        p.Format.Font.Size = 13; p.Format.Font.Bold = true;
        p.Format.SpaceBefore = 10; p.Format.SpaceAfter = 4;
        p.Format.KeepWithNext = true;
    }

    static void Table(Section sec, double width, string[] headers, IEnumerable<string[]> rows, float[] weights)
    {
        var t = sec.AddTable();
        t.Format.Font.Size = 9;
        t.Borders.Bottom.Width = 0.5; t.Borders.Bottom.Color = Colors.LightGray;
        t.TopPadding = t.BottomPadding = t.LeftPadding = t.RightPadding = 3;
        foreach (var w in weights) t.AddColumn(Unit.FromPoint(width * w / weights.Sum()));

        var head = t.AddRow();
        head.HeadingFormat = true; head.Shading.Color = Colors.Gainsboro; head.Format.Font.Bold = true;
        for (var i = 0; i < headers.Length; i++) head.Cells[i].AddParagraph(headers[i]);

        foreach (var row in rows)
        {
            var r = t.AddRow();
            for (var i = 0; i < row.Length; i++) r.Cells[i].AddParagraph(row[i]);
        }
    }
}

// PDFsharp needs a font resolver on Linux containers, which ship no fonts; DejaVu Sans is embedded (free license).
static class PdfFonts
{
    public const string Family = "DejaVu Sans";
    static readonly object Gate = new();
    static bool registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (registered) return;
            GlobalFontSettings.FontResolver = new Resolver();
            registered = true;
        }
    }

    sealed class Resolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "DejaVuSans-Bold" : "DejaVuSans");

        public byte[]? GetFont(string faceName)
        {
            var asm = typeof(PdfFonts).Assembly;
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(faceName + ".ttf", StringComparison.Ordinal));
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
