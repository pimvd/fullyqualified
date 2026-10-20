using System.Text.Json;
using FullyQualified.Web;
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
    /// <summary>Where the app runs, for the free check form. A placeholder until the hosted app has an address.</summary>
    public const string AppOrigin = "https://app.fullyqualified.eu";
    /// <summary>English page and its Dutch counterpart, per kind of page.</summary>
    public static readonly (string Kind, string En, string Nl)[] Pairs =
    [
        ("home", "/", "/nl/"), ("scan", "/scan/", "/nl/check/"), ("support", "/support/", "/nl/support/"), ("about", "/about/", "/nl/over-ons/"),
        ("privacy", "/privacy/", "/nl/privacy/"), ("terms", "/terms/", "/nl/voorwaarden/"),
    ];
    public static readonly string[] Pages = Pairs.SelectMany(p => new[] { p.En, p.Nl }).ToArray();
    public static readonly string[] HomePages = ["/", "/nl/"];
    public static string Lang(string path) => path.StartsWith("/nl/", StringComparison.Ordinal) ? "nl" : "en";
    public static string Counterpart(string path) => Pairs.Select(p => path == p.En ? p.Nl : path == p.Nl ? p.En : null).Single(x => x is not null)!;
    public static string Kind(string path) => Pairs.Single(p => p.En == path || p.Nl == path).Kind;

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
    public static TheoryData<string> HomePages => new(SiteFixture.HomePages);

    static string Text(IElement e) => Regex.Replace(e.TextContent, @"\s+", " ").Trim();

    [Theory, MemberData(nameof(Pages))]
    public async Task Pages_are_served_as_html_with_the_right_language(string path)
    {
        var response = await site.Http.GetAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Set-Cookie"), "The site must not set cookies");
        var doc = await site.Page(path);
        Assert.Equal(SiteFixture.Lang(path), doc.DocumentElement.GetAttribute("lang"));
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
        Assert.Equal(SiteFixture.Lang(path) == "en" ? "en_US" : "nl_NL", SiteFixture.Meta(d, "og:locale"));
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
        var other = SiteFixture.Counterpart(path);
        var (en, nl) = SiteFixture.Lang(path) == "en" ? (path, other) : (other, path);
        var alternates = d.QuerySelectorAll("link[rel=alternate][hreflang]").ToDictionary(l => l.GetAttribute("hreflang")!, l => l.GetAttribute("href")!);
        Assert.Equal(new[] { "en", "nl", "x-default" }, alternates.Keys.Order());
        Assert.Equal(SiteFixture.Origin + en, alternates["en"]);
        Assert.Equal(SiteFixture.Origin + nl, alternates["nl"]);
        Assert.Equal(SiteFixture.Origin + en, alternates["x-default"]);
        Assert.Contains(SiteFixture.Canonical(d), alternates.Values);

        // The other language links back, so the pair is reciprocal, and the visible switch goes to the same place.
        var back = await site.Page(other);
        Assert.Equal(alternates, back.QuerySelectorAll("link[rel=alternate][hreflang]").ToDictionary(l => l.GetAttribute("hreflang")!, l => l.GetAttribute("href")!));
        Assert.Equal(other, d.QuerySelector("a.lang")!.GetAttribute("href"));
        Assert.Equal(SiteFixture.Lang(other), d.QuerySelector("a.lang")!.GetAttribute("hreflang"));
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
        if (SiteFixture.Kind(path) == "home") Assert.NotEmpty(images);
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
        Assert.Empty(d.QuerySelectorAll("iframe, embed, object"));
        foreach (var form in d.QuerySelectorAll("form")) // the one thing that leaves the site: a plain GET to the app's own public checker
        {
            Assert.Equal($"{SiteFixture.AppOrigin}/scan", form.GetAttribute("action"));
            Assert.Equal("get", form.GetAttribute("method"));
        }
        foreach (var e in d.QuerySelectorAll("img[src], link[rel=stylesheet][href], link[rel=icon][href], source[src]"))
        {
            var url = e.GetAttribute("src") ?? e.GetAttribute("href")!;
            Assert.True(url.StartsWith('/'), $"{url} is loaded from outside the site");
        }
        Assert.DoesNotContain("http://", d.Body!.InnerHtml.Replace("http://localhost:8080", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Structured_data_is_valid_and_describes_this_page(string path)
    {
        var d = await site.Page(path);
        var blocks = d.QuerySelectorAll("script[type='application/ld+json']").Select(x => JsonDocument.Parse(x.TextContent).RootElement).ToList();
        Assert.NotEmpty(blocks);
        foreach (var b in blocks)
        {
            Assert.Equal("https://schema.org", b.GetProperty("@context").GetString());
            Assert.Equal(SiteFixture.Lang(path), b.GetProperty("inLanguage").GetString());
            if (b.TryGetProperty("url", out var url)) Assert.Equal(SiteFixture.Canonical(d), url.GetString());
        }

        // A FAQ in the markup must be the FAQ in the structured data, word for word, or search engines show answers the page does not give.
        var visible = d.QuerySelectorAll("#faq details").Select(x => (Text(x.QuerySelector("summary")!), Text(x.QuerySelector("p")!))).ToList();
        var faq = blocks.SingleOrDefault(b => b.GetProperty("@type").GetString() == "FAQPage");
        if (visible.Count > 0)
        {
            Assert.NotEqual(default, faq);
            var structured = faq.GetProperty("mainEntity").EnumerateArray()
                .Select(q => (q.GetProperty("name").GetString()!, q.GetProperty("acceptedAnswer").GetProperty("text").GetString()!)).ToList();
            Assert.Equal(visible, structured);
        }
        else Assert.Equal(default, faq);
    }

    [Theory, MemberData(nameof(HomePages))]
    public async Task The_home_page_describes_the_software_accurately(string path)
    {
        var d = await site.Page(path);
        var blocks = d.QuerySelectorAll("script[type='application/ld+json']").Select(x => JsonDocument.Parse(x.TextContent).RootElement).ToList();
        Assert.Equal(["FAQPage", "SoftwareApplication"], blocks.Select(b => b.GetProperty("@type").GetString()!).Order());
        var app = blocks.Single(b => b.GetProperty("@type").GetString() == "SoftwareApplication");
        Assert.Contains("agpl", app.GetProperty("license").GetString());
        Assert.Equal("0", app.GetProperty("offers").GetProperty("price").GetString());
        Assert.Contains("github.com/pimvd/fullyqualified", app.GetProperty("codeRepository").GetString());
    }

    [Fact]
    public async Task Both_languages_have_the_same_structure_and_different_words()
    {
        foreach (var (kind, enPath, nlPath) in SiteFixture.Pairs)
        {
            var en = await site.Page(enPath);
            var nl = await site.Page(nlPath);
            Assert.Equal(en.QuerySelectorAll("[id]").Select(x => x.Id), nl.QuerySelectorAll("[id]").Select(x => x.Id));
            foreach (var selector in new[] { "section", "h2", "h3", "li", "p", "details", ".card", ".plan", ".steps li", "header nav a", "footer a", "a.button", "pre" })
                Assert.True(en.QuerySelectorAll(selector).Length == nl.QuerySelectorAll(selector).Length, $"{kind}: {selector} differs between languages");
            Assert.NotEqual(en.Title, nl.Title);
            Assert.NotEqual(SiteFixture.Meta(en, "description"), SiteFixture.Meta(nl, "description"));
            Assert.NotEqual(Text(en.QuerySelector("h1")!), Text(nl.QuerySelector("h1")!)); // translated, not copied
        }
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Every_page_links_to_the_others_and_to_the_legal_pages(string path)
    {
        var d = await site.Page(path);
        var lang = SiteFixture.Lang(path);
        var pairs = SiteFixture.Pairs.ToDictionary(p => p.Kind, p => lang == "en" ? p.En : p.Nl);
        var footer = d.QuerySelectorAll("footer a").Select(a => a.GetAttribute("href")).ToList();
        foreach (var kind in new[] { "support", "about", "privacy", "terms" }) Assert.Contains(pairs[kind], footer);
        var header = d.QuerySelectorAll("header a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Contains(pairs["home"], header);
        Assert.Contains(pairs["support"], header);
    }

    [Fact]
    public async Task Sitemap_robots_and_404_are_in_order()
    {
        var sitemap = XDocument.Parse(await site.Http.GetStringAsync("/sitemap.xml"));
        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9", xh = "http://www.w3.org/1999/xhtml";
        var urls = sitemap.Descendants(sm + "url").ToList();
        Assert.Equal(SiteFixture.Pages.Order().Select(p => SiteFixture.Origin + p), urls.Select(u => u.Element(sm + "loc")!.Value).Order());
        foreach (var url in urls)
        {
            var path = url.Element(sm + "loc")!.Value[SiteFixture.Origin.Length..];
            var links = url.Elements(xh + "link").ToDictionary(l => (string)l.Attribute("hreflang")!, l => (string)l.Attribute("href")!);
            Assert.Equal(["en", "nl", "x-default"], links.Keys.Order());
            Assert.Contains(SiteFixture.Origin + path, links.Values);
        }

        // Every HTML page that should be indexed is listed, and nothing else is.
        var indexable = Directory.GetFiles(site.SiteDir, "index.html", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(site.SiteDir, Path.GetDirectoryName(f)!).Replace('\\', '/'))
            .Select(dir => dir == "." ? "/" : "/" + dir + "/").Order().ToList();
        Assert.Equal(SiteFixture.Pages.Order(), indexable);
        Assert.Equal(new[] { "404.html" }, Directory.GetFiles(site.SiteDir, "*.html", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(n => n != "index.html"));

        var robots = await site.Http.GetStringAsync("/robots.txt");
        Assert.Contains($"Sitemap: {SiteFixture.Origin}/sitemap.xml", robots);
        Assert.DoesNotContain("Disallow: /", robots.Replace("Disallow:\n", ""), StringComparison.Ordinal);

        var notFound = new HtmlParser().ParseDocument(await site.Http.GetStringAsync("/404.html"));
        Assert.Equal("noindex", notFound.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
    }

    [Fact]
    public async Task The_site_does_not_promise_prices_that_do_not_exist()
    {
        foreach (var path in SiteFixture.HomePages)
        {
            var pricing = Text((await site.Page(path)).QuerySelector("#pricing")!);
            Assert.DoesNotMatch(@"[€$£]|\bEUR\b|per month|per maand|/mo", pricing);
        }
        foreach (var path in SiteFixture.Pages) // the terms must not state a price either while none is set
            Assert.DoesNotMatch(@"[€$£]\s?\d", Text((await site.Page(path)).QuerySelector("main")!));
    }

    [Fact]
    public async Task Plans_match_the_product_plan_and_the_defaults_in_the_app()
    {
        // docs/PLAN.md is the source of truth for tiers, domain limits and history; the app starts from the same numbers.
        var plan = await File.ReadAllTextAsync(Path.Combine(site.Root, "docs", "PLAN.md"));
        var tiers = Regex.Matches(plan, @"^\| (Free|Starter|Business) \| (\d+) \| (\d+) months? \|", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value))).ToList();
        Assert.Equal(3, tiers.Count);
        Assert.Contains("| Custom |", plan);
        foreach (var path in SiteFixture.HomePages)
        {
            var cards = (await site.Page(path)).QuerySelectorAll(".plan").ToList();
            Assert.Equal(4, cards.Count);
            var shown = cards.Take(3).Select(p => (Text(p.QuerySelector("h3")!), Text(p.QuerySelector(".domains")!).Split(' ')[0], int.Parse(Regex.Match(Text(p.QuerySelectorAll("li").Last()), @"\d+").Value))).ToList();
            Assert.Equal(tiers, shown);
            Assert.Equal("Custom", Text(cards[3].QuerySelector("h3")!));
        }
        foreach (var (name, domains, months) in tiers)
            Assert.Equal((int.Parse(domains), months), (Tiers.Defaults.Single(t => t.Name == name).MaxDomains, Tiers.Defaults.Single(t => t.Name == name).RetentionMonths));
    }

    [Fact]
    public async Task The_self_hosting_command_is_the_one_in_the_readme()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(site.Root, "README.md"));
        foreach (var path in SiteFixture.HomePages)
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

    [Theory]
    [InlineData("/privacy/")]
    [InlineData("/nl/privacy/")]
    [InlineData("/terms/")]
    [InlineData("/nl/voorwaarden/")]
    public async Task Legal_pages_say_they_are_drafts_until_a_lawyer_has_filled_them_in(string path)
    {
        // When the texts are final, remove the notice and the [placeholders] and delete this test.
        var d = await site.Page(path);
        var notice = Text(d.QuerySelector(".notice")!);
        Assert.Matches(@"^(Draft|Concept):", notice);
        Assert.Contains("[", Text(d.QuerySelector("main")!));
        Assert.InRange(d.QuerySelectorAll("article > .wrap > section").Length, 8, 14);
        Assert.Equal(d.QuerySelectorAll("article section[id]").Select(x => x.Id), d.QuerySelectorAll(".toc a").Select(a => a.GetAttribute("href")![1..]));
    }

    [Fact]
    public async Task Placeholders_appear_on_the_legal_pages_only()
    {
        foreach (var path in SiteFixture.Pages.Where(p => SiteFixture.Kind(p) is not ("privacy" or "terms")))
        {
            var text = Text((await site.Page(path)).QuerySelector("main")!);
            Assert.DoesNotMatch(@"\[[^\]]+\]", text);
            Assert.DoesNotContain("TODO", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_privacy_statement_matches_what_the_site_and_the_app_actually_do()
    {
        var home = await site.Page("/");
        Assert.Empty(home.QuerySelectorAll("script[src]"));
        foreach (var path in new[] { "/privacy/", "/nl/privacy/" })
        {
            var text = Text((await site.Page(path)).QuerySelector("main")!);
            Assert.Contains("crt.sh", text);                         // CtLogCheck sends domain names there
            Assert.Contains("passkey", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("24", text);                             // recovery links live 24 hours
            Assert.Contains("7", text);                              // invitations live 7 days
        }
        Assert.Equal(24, AdminService.RecoveryLifetime.TotalHours);
        Assert.Equal(7, AdminService.InviteLifetime.TotalDays);
        var ct = await File.ReadAllTextAsync(Path.Combine(site.Root, "src", "FullyQualified.Web", "Checks", "CtLogCheck.cs"));
        Assert.Contains("crt.sh", ct);
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Setting_names_and_commands_in_the_pages_exist_in_the_product(string path)
    {
        var text = Text((await site.Page(path)).QuerySelector("main")!);
        var source = string.Join("\n", Directory.GetFiles(Path.Combine(site.Root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)) + await File.ReadAllTextAsync(Path.Combine(site.Root, "README.md"));
        foreach (Match m in Regex.Matches(text, @"\b(?:Sso|Scanning|Alerts|Auth|PublicScan):[A-Za-z]+(?::[A-Za-z]+)?"))
            Assert.True(source.Contains(m.Value), $"{m.Value} on {path} is not a setting of the product");

        if (SiteFixture.Kind(path) != "support") return;
        // The recovery command works the way the page says: service name from compose, assembly from the build, sub-command from the app.
        var command = Regex.Match(text, @"docker compose exec (\S+) dotnet (\S+) recover");
        Assert.True(command.Success);
        Assert.Contains($"{command.Groups[1].Value}:", await File.ReadAllTextAsync(Path.Combine(site.Root, "docker-compose.yml")));
        Assert.Equal(typeof(Program).Assembly.GetName().Name + ".dll", command.Groups[2].Value);
        Assert.Contains("args is [\"recover\"", await File.ReadAllTextAsync(Path.Combine(site.Root, "src", "FullyQualified.Web", "Program.cs")));
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"FullyQualified.Web.dll\"]", await File.ReadAllTextAsync(Path.Combine(site.Root, "Dockerfile")));
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task Roles_and_lifetimes_named_in_the_pages_are_the_real_ones(string path)
    {
        if (SiteFixture.Kind(path) != "support") return;
        var text = Text((await site.Page(path)).QuerySelector("main")!).ToLowerInvariant();
        Assert.Equal(["Viewer", "Member", "Admin"], Enum.GetNames<Role>());
        Assert.Contains(SiteFixture.Lang(path) == "en" ? "viewer" : "lezer", text);
        Assert.Contains("24", text);
        var queue = await File.ReadAllTextAsync(Path.Combine(site.Root, "src", "FullyQualified.Web", "Services", "JobQueue.cs"));
        Assert.Contains("TimeSpan.FromMinutes(15)", queue);
        Assert.Contains("\"Scanning:IntervalHours\", 6.0", queue);
    }

    [Theory, MemberData(nameof(Pages))]
    public async Task The_free_check_form_is_the_same_everywhere_and_points_at_the_app(string path)
    {
        var d = await site.Page(path);
        var forms = d.QuerySelectorAll("form").ToList();
        if (SiteFixture.Kind(path) is not ("home" or "scan")) { Assert.Empty(forms); return; }
        var form = Assert.Single(forms);
        Assert.Equal($"{SiteFixture.AppOrigin}/scan", form.GetAttribute("action"));
        Assert.Equal("get", form.GetAttribute("method"));
        var domain = form.QuerySelector("input[name=domain]")!;
        Assert.NotNull(domain.GetAttribute("required"));
        Assert.Equal("253", domain.GetAttribute("maxlength"));
        Assert.Equal(domain.Id, form.QuerySelector("label")!.GetAttribute("for"));
        var lang = SiteFixture.Lang(path);
        Assert.Equal(lang, form.QuerySelector("input[name=culture]")!.GetAttribute("value"));       // the app opens in the visitor's language
        Assert.Equal(lang, form.QuerySelector("input[name=ui-culture]")!.GetAttribute("value"));
        Assert.Equal(new[] { "domain", "culture", "ui-culture" }, form.QuerySelectorAll("input").Select(i => i.GetAttribute("name")).ToArray());
        Assert.NotNull(form.QuerySelector("button[type=submit]"));
    }

    [Fact]
    public async Task The_free_check_form_matches_what_the_app_accepts()
    {
        // The route exists in the app, is anonymous, and is off unless switched on; the site's wording about limits matches the code.
        var route = typeof(FullyQualified.Web.Components.Pages.PublicScan).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false).Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Single();
        Assert.Equal("/scan", route.Template);
        Assert.NotEmpty(typeof(FullyQualified.Web.Components.Pages.PublicScan).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false));
        foreach (var path in new[] { "/scan/", "/nl/check/" })
        {
            var text = Text((await site.Page(path)).QuerySelector("main")!);
            Assert.Contains("443", text);
            Assert.Contains(SiteFixture.Lang(path) == "en" ? "an hour" : "een uur", text);
        }
        Assert.Null(FullyQualified.Web.PublicScanService.Normalize("example.com:8443")); // port 443 only, as the page says
    }

    [Theory]
    [InlineData("/privacy/", "free check", "an hour")]
    [InlineData("/nl/privacy/", "gratis check", "een uur")]
    public async Task The_privacy_statement_covers_the_free_check(string path, string name, string hour)
    {
        var text = Text((await site.Page(path)).QuerySelector("main")!).ToLowerInvariant();
        Assert.Contains(name, text);
        Assert.Contains(hour, text);
    }
}
