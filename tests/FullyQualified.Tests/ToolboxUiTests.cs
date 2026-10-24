using System.Net;
using AngleSharp.Html.Parser;

namespace FullyQualified.Tests;

public class ToolboxUiTests(RoleFactory factory) : IClassFixture<RoleFactory>
{
    [Theory]
    [InlineData("Viewer")]
    [InlineData("Member")]
    [InlineData("Admin")]
    public async Task Signed_in_roles_can_choose_every_diagnostic_without_a_database(string role)
    {
        var response = await factory.As(role).GetAsync("/toolbox");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        var form = page.QuerySelector("#toolbox-form")!;
        Assert.NotNull(form);
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/api/toolbox", form.GetAttribute("action"));
        Assert.Equal("off", form.GetAttribute("autocomplete"));
        Assert.NotEmpty(form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!);
        Assert.Equal(new[] { "tls", "starttls", "mx", "certificate", "dns", "spf", "dkim", "dmarc", "dmarc-builder", "headers", "mta-sts" },
            form.QuerySelectorAll("select[name=Tool] option").Select(x => x.GetAttribute("value")));
        foreach (var control in form.QuerySelectorAll("input:not([type=hidden]),select,textarea"))
        {
            Assert.NotEmpty(control.Id!);
            Assert.NotNull(form.QuerySelector($"label[for='{control.Id}']"));
        }
        Assert.NotNull(page.QuerySelector("#toolbox-status[role=status][aria-live=polite]"));
        Assert.NotNull(page.QuerySelector("#toolbox-cancel[type=button]"));
        Assert.NotNull(page.QuerySelector("#toolbox-reset[type=reset]"));
        Assert.NotNull(page.QuerySelector("script[src='toolbox.js']"));
    }

    [Fact]
    public async Task Anonymous_visitors_cannot_reach_the_toolbox()
    {
        var response = await factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/toolbox");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Viewer", false)]
    [InlineData("Member", false)]
    [InlineData("Admin", true)]
    public async Task Only_admins_see_the_sending_test_link(string role, bool expected)
    {
        var response = await factory.As(role).GetAsync("/toolbox");
        var page = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, page.QuerySelector("#toolbox a[href='/email-test']") is not null);
    }

    [Fact]
    public async Task Dutch_and_English_pages_localize_the_tool_labels()
    {
        async Task<string> Label(string culture)
        {
            var client = factory.As("Viewer");
            client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
            var page = new HtmlParser().ParseDocument(await (await client.GetAsync("/toolbox")).Content.ReadAsStringAsync());
            Assert.DoesNotContain("Tool_", page.DocumentElement.TextContent);
            return page.QuerySelector("label[for=toolbox-tool]")!.TextContent;
        }
        Assert.Equal("Choose a tool", await Label("en"));
        Assert.Equal("Kies een hulpmiddel", await Label("nl"));
    }
}
