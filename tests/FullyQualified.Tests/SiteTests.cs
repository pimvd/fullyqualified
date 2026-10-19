using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FullyQualified.Tests;

/// <summary>Serves the marketing site from the repository over real HTTP, the way a static host would, and crawls it.</summary>
public class SiteFixture : IAsyncLifetime
{
    public const string Origin = "https://fullyqualified.eu";
    public static readonly string[] Pages = ["/", "/nl/"];

    WebApplication? app;
    public string Root { get; } = FindRoot();
    public string SiteDir => Path.Combine(Root, "site");
    public HttpClient Http { get; private set; } = null!;

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullyQualified.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        var files = new PhysicalFileProvider(SiteDir);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        if (app is not null) await app.DisposeAsync();
    }

    public async Task<IDocument> Page(string path) =>
        await new HtmlParser().ParseDocumentAsync(await Http.GetStringAsync(path));

    /// <summary>Maps an absolute production URL, or a site-relative one, to a path on the local server.</summary>
    public static string Local(string url) =>
        url.StartsWith(Origin, StringComparison.Ordinal) ? url[Origin.Length..] is { Length: > 0 } p ? p : "/" : url;

    public static string Canonical(IDocument d) => d.QuerySelector("link[rel=canonical]")!.GetAttribute("href")!;
    public static string Meta(IDocument d, string key) =>
        d.QuerySelector($"meta[name='{key}'], meta[property='{key}']")?.GetAttribute("content") ?? "";
}

public class SiteTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    public static TheoryData<string> Pages => new(SiteFixture.Pages);

    static string Text(IElement e) => Regex.Replace(e.TextContent, @"\s+", " ").Trim();

    [Theory, MemberData(nameof(Pages))]
    public async Task Pages_are_served_as_html_with_the_right_language(string path)
    {
        var response = await site.Http.GetAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Set-Cookie"), "The site must not set cookies");
        var doc = await site.Page(path);
        Assert.Equal(path == "/" ? "en" : "nl", doc.DocumentElement.GetAttribute("lang"));
        Assert.Single(doc.QuerySelectorAll("h1"));
        Assert.NotNull(doc.QuerySelector("meta[name=viewport]"));
        Assert.NotNull(doc.QuerySelector("a.skip[href='#main']"));
        Assert.NotNull(doc.QuerySelector("main#main"));
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Search_and_sharing_metadata_is_complete(string path)
    {
        var d = await site.Page(path);
        Assert.InRange(d.Title!.Length, 30, 65);
        Assert.InRange(SiteFixture.Meta(d, "description").Length, 70, 160);
        Assert.Equal(SiteFixture.Origin + path, SiteFixture.Canonical(d));

        Assert.Equal(d.Title, SiteFixture.Meta(d, "og:title"));
        Assert.Equal(SiteFixture.Meta(d, "description"), SiteFixture.Meta(d, "og:description"));
        Assert.Equal(SiteFixture.Canonical(d), SiteFixture.Meta(d, "og:url"));
        Assert.Equal("summary_large_image", SiteFixture.Meta(d, "twitter:card"));
        Assert.Equal(path == "/" ? "en_US" : "nl_NL", SiteFixture.Meta(d, "og:locale"));
        Assert.NotEmpty(SiteFixture.Meta(d, "og:image:alt"));

        // The share image exists, is a PNG and is the size the tags claim.
        var image = SiteFixture.Meta(d, "og:image");
        Assert.StartsWith(SiteFixture.Origin + "/", image);
        var bytes = await site.Http.GetByteArrayAsync(SiteFixture.Local(image));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal(1200, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(630, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Language_alternates_point_at_each_other(string path)
    {
        var d = await site.Page(path);
        var alternates = d.QuerySelectorAll("link[rel=alternate][hreflang]").ToDictionary(l => l.GetAttribute("hreflang")!, l => l.GetAttribute("href")!);
        Assert.Equal(new[] { "en", "nl", "x-default" }, alternates.Keys.Order());
        Assert.Equal(SiteFixture.Origin + "/", alternates["x-default"]);
        Assert.Contains(SiteFixture.Canonical(d), alternates.Values);

        // The other language links back, so the pair is reciprocal.
        var other = await site.Page(SiteFixture.Local(alternates[path == "/" ? "nl" : "en"]));
        Assert.Equal(SiteFixture.Canonical(d), other.QuerySelector("link[rel=alternate][hreflang=" + (path == "/" ? "en" : "nl") + "]")!.GetAttribute("href"));
        Assert.Equal(SiteFixture.Local(alternates[path == "/" ? "nl" : "en"]), d.QuerySelector("a.lang")!.GetAttribute("href"));
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Every_internal_link_asset_and_anchor_resolves(string path)
    {
        var d = await site.Page(path);
        var ids = d.QuerySelectorAll("[id]").Select(e => e.Id).ToHashSet();
        var targets = d.QuerySelectorAll("a[href], link[href], img[src]")
            .Select(e => e.GetAttribute("href") ?? e.GetAttribute("src")!)
            .Where(u => !u.StartsWith("https://github.com") && !u.StartsWith("mailto:"))
            .Distinct().ToList();
        Assert.NotEmpty(targets);

        foreach (var url in targets)
        {
            if (url.StartsWith('#')) { Assert.True(ids.Contains(url[1..]), $"Anchor {url} has no target on {path}"); continue; }
            var local = SiteFixture.Local(url);
            Assert.True(local.StartsWith('/'), $"{url} is neither internal nor an allowed external link");
            var response = await site.Http.GetAsync(local.Split('#')[0]);
            Assert.True(response.IsSuccessStatusCode, $"{url} on {path} returned {(int)response.StatusCode}");
        }
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Images_have_alt_text_and_dimensions(string path)
    {
        var images = (await site.Page(path)).QuerySelectorAll("img").ToList();
        Assert.NotEmpty(images);
        foreach (var img in images)
        {
            Assert.False(string.IsNullOrWhiteSpace(img.GetAttribute("alt")), $"{img.GetAttribute("src")} has no alt text");
            Assert.NotNull(img.GetAttribute("width"));
            Assert.NotNull(img.GetAttribute("height"));
        }
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task The_page_loads_nothing_from_third_parties(string path)
    {
        var d = await site.Page(path);
        Assert.Empty(d.QuerySelectorAll("script[src]"));
        Assert.Empty(d.QuerySelectorAll("iframe, embed, object, form"));
        foreach (var e in d.QuerySelectorAll("img[src], link[rel=stylesheet][href], link[rel=icon][href], source[src]"))
        {
            var url = e.GetAttribute("src") ?? e.GetAttribute("href")!;
            Assert.True(url.StartsWith('/'), $"{url} is loaded from outside the site");
        }
        Assert.DoesNotContain("http://", d.Body!.InnerHtml.Replace("http://localhost:8080", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Structured_data_is_valid_and_matches_what_visitors_read(string path)
    {
        var d = await site.Page(path);
        var blocks = d.QuerySelectorAll("script[type='application/ld+json']").Select(s => JsonDocument.Parse(s.TextContent).RootElement).ToList();
        Assert.Equal(["FAQPage", "SoftwareApplication"], blocks.Select(b => b.GetProperty("@type").GetString()!).Order());

        var app = blocks.Single(b => b.GetProperty("@type").GetString() == "SoftwareApplication");
        Assert.Equal(SiteFixture.Canonical(d), app.GetProperty("url").GetString());
        Assert.Contains("agpl", app.GetProperty("license").GetString());
        Assert.Equal("0", app.GetProperty("offers").GetProperty("price").GetString());

        var structured = blocks.Single(b => b.GetProperty("@type").GetString() == "FAQPage").GetProperty("mainEntity").EnumerateArray()
            .Select(q => (q.GetProperty("name").GetString()!, q.GetProperty("acceptedAnswer").GetProperty("text").GetString()!)).ToList();
        var visible = d.QuerySelectorAll("#faq details").Select(x => (Text(x.QuerySelector("summary")!), Text(x.QuerySelector("p")!))).ToList();
        Assert.NotEmpty(visible);
        Assert.Equal(visible, structured);
    }

    [Fact]
    public async Task Both_languages_have_the_same_structure_and_different_words()
    {
        var en = await site.Page("/");
        var nl = await site.Page("/nl/");
        Assert.Equal(en.QuerySelectorAll("section[id]").Select(s => s.Id), nl.QuerySelectorAll("section[id]").Select(s => s.Id));
        Assert.Equal(en.QuerySelectorAll("header nav a").Select(a => a.GetAttribute("href")), nl.QuerySelectorAll("header nav a").Select(a => a.GetAttribute("href")));
        foreach (var selector in new[] { "#faq details", ".steps li", ".plan", "#checks .card", "#why .card", ".trust li" })
            Assert.Equal(en.QuerySelectorAll(selector).Length, nl.QuerySelectorAll(selector).Length);
        Assert.NotEqual(Text(en.QuerySelector("h1")!), Text(nl.QuerySelector("h1")!));
        Assert.NotEqual(en.Title, nl.Title);
        Assert.NotEqual(SiteFixture.Meta(en, "description"), SiteFixture.Meta(nl, "description"));
        Assert.NotEqual(Text(en.QuerySelector("#faq summary")!), Text(nl.QuerySelector("#faq summary")!));
    }

    [Fact]
    public async Task Sitemap_robots_and_404_are_in_order()
    {
        var sitemap = XDocument.Parse(await site.Http.GetStringAsync("/sitemap.xml"));
        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9", xh = "http://www.w3.org/1999/xhtml";
        var urls = sitemap.Descendants(sm + "url").ToList();
        Assert.Equal(SiteFixture.Pages.Select(p => SiteFixture.Origin + p), urls.Select(u => u.Element(sm + "loc")!.Value));
        foreach (var url in urls)
            Assert.Equal(["en", "nl", "x-default"], url.Elements(xh + "link").Select(l => (string)l.Attribute("hreflang")!).Order());

        // Every HTML file that should be indexed is listed, and nothing else is.
        var indexable = Directory.GetFiles(site.SiteDir, "*.html", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("404.html"))
            .Select(f => "/" + Path.GetRelativePath(site.SiteDir, Path.GetDirectoryName(f)!).Replace('\\', '/').Replace(".", "")).Select(p => p.TrimEnd('/') + "/").Select(p => p == "//" ? "/" : p)
            .Order().ToList();
        Assert.Equal(indexable, SiteFixture.Pages.Order());

        var robots = await site.Http.GetStringAsync("/robots.txt");
        Assert.Contains($"Sitemap: {SiteFixture.Origin}/sitemap.xml", robots);
        Assert.DoesNotContain("Disallow: /", robots.Replace("Disallow:\n", ""), StringComparison.Ordinal);

        var notFound = new HtmlParser().ParseDocument(await site.Http.GetStringAsync("/404.html"));
        Assert.Equal("noindex", notFound.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
    }

    [Fact]
    public async Task The_site_does_not_promise_prices_that_do_not_exist()
    {
        foreach (var path in SiteFixture.Pages)
        {
            var pricing = Text((await site.Page(path)).QuerySelector("#pricing")!);
            Assert.DoesNotMatch(@"[€$£]|\bEUR\b|per month|per maand|/mo", pricing);
        }
    }

    [Fact]
    public async Task Plans_match_the_product_plan()
    {
        // docs/PLAN.md is the source of truth for tiers and domain limits.
        var plan = await File.ReadAllTextAsync(Path.Combine(site.Root, "docs", "PLAN.md"));
        var tiers = Regex.Matches(plan, @"^\| (Free|Starter|Business) \| (\d+) \|", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(3, tiers.Count);
        foreach (var path in SiteFixture.Pages)
        {
            var shown = (await site.Page(path)).QuerySelectorAll(".plan").Select(p => (Text(p.QuerySelector("h3")!), Text(p.QuerySelector(".domains")!).Split(' ')[0])).ToList();
            Assert.Equal(tiers, shown);
        }
    }

    [Fact]
    public async Task The_self_hosting_command_is_the_one_in_the_readme()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(site.Root, "README.md"));
        foreach (var path in SiteFixture.Pages)
        {
            var code = Text((await site.Page(path)).QuerySelector("#selfhost pre")!);
            Assert.EndsWith("docker compose up", code);
            Assert.Contains("docker compose up", readme);
            Assert.Contains("github.com/pimvd/fullyqualified", code);
        }
        Assert.True(File.Exists(Path.Combine(site.Root, "docker-compose.yml")));
    }

    [Fact]
    public async Task The_report_is_presented_as_evidence_and_not_as_compliance()
    {
        foreach (var (path, phrase) in new[] { ("/", "not a compliance certificate"), ("/nl/", "geen nalevingscertificaat") })
            Assert.Contains(phrase, Text((await site.Page(path)).QuerySelector("#report .note")!));
    }
}
