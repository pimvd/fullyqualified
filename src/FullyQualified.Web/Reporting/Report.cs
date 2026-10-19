using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FullyQualified.Web;

public record DomainControl(string Domain, bool Spf, bool Dmarc, bool Caa, bool Dnssec);
public record CertRow(string Host, string Issuer, DateTimeOffset NotAfter, int DaysLeft, string Source, string Renewal);
public record ChangeRow(DateTimeOffset At, string Asset, string Check, string Key, string Before, string After);
public record ReportModel(DateTimeOffset GeneratedAt, int Assets, int Certificates, int ExpiringSoon, int Expired,
    List<DomainControl> Domains, List<CertRow> Certs, List<ChangeRow> Changes);

public class ReportBuilder(AppDbContext db)
{
    public async Task<ReportModel> BuildAsync()
    {
        var assets = await db.Assets.ToListAsync();
        var names = assets.ToDictionary(a => a.Id, a => a.Name);
        var latest = await db.LatestResultsAsync();
        var certs = await db.Certificates.OrderBy(c => c.NotAfter).ToListAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var changes = await db.Changes.Where(c => c.DetectedAt >= since).OrderByDescending(c => c.DetectedAt).Take(500).ToListAsync();

        var domains = assets.Where(a => a.Kind == AssetKind.Domain).OrderBy(a => a.Name).Select(a =>
        {
            var v = latest.FirstOrDefault(r => r.AssetId == a.Id && r.CheckType == "dns")?.Values ?? [];
            return new DomainControl(a.Name, Has(v, "spf"), Has(v, "dmarc"), Has(v, "caa"), v.GetValueOrDefault("dnssec") == "yes");
        }).ToList();

        // CT lists the same certificate under several hosts; the inventory counts distinct certificates.
        var rows = certs.Select(c => new CertRow(c.Host, c.Issuer, c.NotAfter, c.DaysLeft, c.Source, c.Renewal)).ToList();
        return new(DateTimeOffset.UtcNow, assets.Count, certs.Count, certs.Count(c => c.DaysLeft is >= 0 and <= 30),
            certs.Count(c => c.DaysLeft < 0), domains, rows,
            changes.Select(c => new ChangeRow(c.DetectedAt, names.GetValueOrDefault(c.AssetId, "?"), c.CheckType, c.Key, c.Before, c.After)).ToList());
    }

    static bool Has(Dictionary<string, string> v, string key) => !string.IsNullOrEmpty(v.GetValueOrDefault(key));
}

public static class ReportWriter
{
    static string YesNo(IStringLocalizer l, bool b) => l[b ? "Rep_Yes" : "Rep_No"];

    public static byte[] Excel(ReportModel m, IStringLocalizer l)
    {
        using var wb = new XLWorkbook();
        var s = wb.AddWorksheet(Trunc(l["Rep_Summary"]));
        s.Cell(1, 1).Value = l["Rep_Title"].Value; s.Cell(1, 1).Style.Font.Bold = true;
        (string, object)[] summary =
        [
            (l["Rep_GeneratedAt"], m.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")), (l["Rep_Assets"], m.Assets),
            (l["Rep_Certs"], m.Certificates), (l["Rep_Expiring"], m.ExpiringSoon), (l["Rep_Expired"], m.Expired),
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
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Header().Column(h =>
            {
                h.Item().Text(l["Rep_Title"].Value).FontSize(20).Bold();
                h.Item().Text($"{l["Rep_GeneratedAt"]}: {m.GeneratedAt:yyyy-MM-dd HH:mm} UTC").FontColor(Colors.Grey.Darken1);
            });
            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(10);
                col.Item().Text(l["Rep_Summary"].Value).FontSize(13).Bold();
                col.Item().Text($"{l["Rep_Assets"]}: {m.Assets}   {l["Rep_Certs"]}: {m.Certificates}   {l["Rep_Expiring"]}: {m.ExpiringSoon}   {l["Rep_Expired"]}: {m.Expired}");

                col.Item().Text(l["Rep_DnsControls"].Value).FontSize(13).Bold();
                Table(col, [l["Col_Domain"], l["Ctl_Spf"], l["Ctl_Dmarc"], l["Ctl_Caa"], l["Ctl_Dnssec"]],
                    m.Domains.Select(d => new[] { d.Domain, YesNo(l, d.Spf), YesNo(l, d.Dmarc), YesNo(l, d.Caa), YesNo(l, d.Dnssec) }), [3, 1, 1, 1, 1]);

                col.Item().Text(l["Nav_Certificates"].Value).FontSize(13).Bold();
                Table(col, [l["Col_Name"], l["Col_Issuer"], l["Col_Expires"], l["Col_Days"], l["Col_Renewal"]],
                    m.Certs.Select(c => new[] { c.Host, c.Issuer, c.NotAfter.ToString("yyyy-MM-dd"), c.DaysLeft.ToString(), l["Renewal_" + c.Renewal].Value }), [3, 2, 1.4f, 1, 1.6f]);

                col.Item().Text(l["Rep_ChangesTitle"].Value).FontSize(13).Bold();
                Table(col, [l["Col_Detected"], l["Col_Asset"], l["Col_Change"], l["Col_Before"], l["Col_After"]],
                    m.Changes.Take(100).Select(c => new[] { c.At.ToString("yyyy-MM-dd"), c.Asset, c.Key, Cut(c.Before), Cut(c.After) }), [1.4f, 2, 1, 2.5f, 2.5f]);
            });
            page.Footer().AlignCenter().Text(t => { t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
        })).GeneratePdf();
    }

    static string Cut(string s) => s.Length > 60 ? s[..60] + "…" : s;

    static void Table(ColumnDescriptor col, string[] headers, IEnumerable<string[]> rows, float[] widths) =>
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c => { foreach (var w in widths) c.RelativeColumn(w); });
            t.Header(h => { foreach (var x in headers) h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(x).Bold(); });
            foreach (var row in rows)
                foreach (var cell in row)
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(cell);
        });
}
