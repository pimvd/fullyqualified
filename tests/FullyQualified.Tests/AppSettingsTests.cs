using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullyQualified.Tests;

public class AppSettingsCatalogTests
{
    [Fact]
    public void Every_setting_is_unique_belongs_to_a_known_group_and_is_complete()
    {
        Assert.Equal(AppSettingsCatalog.All.Count, AppSettingsCatalog.All.Select(d => d.Key.ToLowerInvariant()).Distinct().Count());
        Assert.All(AppSettingsCatalog.All, d =>
        {
            Assert.Contains(d.Group, AppSettingsCatalog.Groups);
            if (d.Kind == SettingKind.Choice) { Assert.NotEmpty(d.Choices!); Assert.Contains(d.Default, d.Choices!); }
            if (d.Kind == SettingKind.Number) { Assert.True(d.Min <= d.Max); Assert.Null(AppSettingsCatalog.Validate(d, d.Default)); }
        });
    }

    [Theory]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("Hosting:Role")]
    [InlineData("Database:AutoMigrate")]
    public void The_database_connection_and_the_role_are_never_kept_in_the_database(string key) => Assert.Null(AppSettingsCatalog.Find(key));

    [Theory]
    [InlineData("Alerts:Email:Port", "587", null)]
    [InlineData("Alerts:Email:Port", "0", "invalid_number")]
    [InlineData("Alerts:Email:Port", "99999", "invalid_number")]
    [InlineData("Alerts:Email:Port", "abc", "invalid_number")]
    [InlineData("Alerts:Email:Ssl", "true", null)]
    [InlineData("Alerts:Email:Ssl", "yes", "invalid_bool")]
    [InlineData("Appearance:Theme", "christmas", null)]
    [InlineData("Appearance:Theme", "halloween", "invalid_choice")]
    [InlineData("Alerts:Email:Host", "", null)]               // empty on purpose is allowed for text
    [InlineData("Scanning:IntervalHours", "0.5", null)]
    public void Values_are_checked_against_the_kind_of_setting(string key, string value, string? error) =>
        Assert.Equal(error, AppSettingsCatalog.Validate(AppSettingsCatalog.Find(key)!, value));

    [Fact]
    public void Every_setting_and_group_has_a_label_in_english_and_dutch()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullyQualified.slnx"))) dir = dir.Parent;
        foreach (var file in new[] { "SharedResource.resx", "SharedResource.nl.resx" })
        {
            var names = XDocument.Load(Path.Combine(dir!.FullName, "src", "FullyQualified.Web", "Resources", file)).Descendants("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
            Assert.All(AppSettingsCatalog.All, d => Assert.True(names.Contains(d.ResourceKey), $"{file} lacks {d.ResourceKey}"));
            Assert.All(AppSettingsCatalog.Groups, g => Assert.True(names.Contains("SettingGroup_" + g), $"{file} lacks SettingGroup_{g}"));
        }
    }
}

/// <summary>The environment says 9; the database may say otherwise.</summary>
public class PgSettingsFactory : PgFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Scanning:ManualCooldownMinutes", "9");
    }
}

public class AppSettingsDbTests(PgSettingsFactory factory) : OrgTestBase<PgSettingsFactory>(factory), IClassFixture<PgSettingsFactory>
{
    IConfiguration Cfg => Factory.Services.GetRequiredService<IConfiguration>();

    async Task<List<AppSetting>> Rows()
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.AsNoTracking().ToListAsync();
    }

    async Task Clean()
    {
        await EnsureOwner();
        using (var scope = Scope()) await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.ExecuteDeleteAsync();
        Factory.Services.GetRequiredService<DbSettings>().Refresh();
    }

    async Task<HttpResponseMessage> Put(HttpClient client, Dictionary<string, string?>? set = null, string[]? reset = null) =>
        await client.PutAsJsonAsync("/platform/settings/", new { set, reset });

    [DbFact]
    public async Task A_value_in_the_database_overrides_the_environment_and_resetting_brings_the_environment_back()
    {
        await Clean();
        var owner = Factory.As(Owner);
        Assert.Equal("9", Cfg["Scanning:ManualCooldownMinutes"]);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(owner, new() { ["Scanning:ManualCooldownMinutes"] = "3" })).StatusCode);
        Assert.Equal("3", Cfg["Scanning:ManualCooldownMinutes"]);          // live, no restart

        Assert.Equal(HttpStatusCode.NoContent, (await Put(owner, reset: ["Scanning:ManualCooldownMinutes"])).StatusCode);
        Assert.Equal("9", Cfg["Scanning:ManualCooldownMinutes"]);
        Assert.Empty(await Rows());
    }

    [DbFact]
    public async Task A_row_outside_the_catalog_never_overrides_anything()
    {
        await Clean();
        var before = Cfg.GetConnectionString("Default");
        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppSettings.Add(new AppSetting { Key = "ConnectionStrings:Default", Value = "Host=evil" });
            db.AppSettings.Add(new AppSetting { Key = "Hosting:Role", Value = "worker" });
            await db.SaveChangesAsync();
        }
        Factory.Services.GetRequiredService<DbSettings>().Refresh();
        Assert.Equal(before, Cfg.GetConnectionString("Default"));
        Assert.NotEqual("worker", Cfg["Hosting:Role"]);
        await Clean();
    }

    [DbFact]
    public async Task Secrets_are_stored_protected_read_back_by_the_app_and_never_shown()
    {
        await Clean();
        Assert.Equal(HttpStatusCode.NoContent, (await Put(Factory.As(Owner), new() { ["Alerts:Email:Password"] = "hunter2-secret" })).StatusCode);

        var row = Assert.Single(await Rows());
        Assert.True(row.IsSecret);
        Assert.DoesNotContain("hunter2-secret", row.Value);                  // protected at rest
        Assert.Equal("hunter2-secret", Cfg["Alerts:Email:Password"]);         // the mail code still reads it as usual

        using var scope = Scope();
        var view = (await scope.ServiceProvider.GetRequiredService<AppSettingsService>().ListAsync()).Single(v => v.Def.Key == "Alerts:Email:Password");
        Assert.Equal(SettingSource.Database, view.Source);
        Assert.True(view.IsSet);
        Assert.Null(view.Value);                                               // the page never gets the secret
    }

    [DbFact]
    public async Task Only_the_platform_owner_may_change_settings()
    {
        await Clean();
        var org = await NewOrg();
        var body = new Dictionary<string, string?> { ["Scanning:ManualCooldownMinutes"] = "1" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(Factory.As(org.Admin), body)).StatusCode);       // an organization's own admin
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(Factory.As(org.Viewer), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(Factory.As(null), body)).StatusCode);
        Assert.Empty(await Rows());
        Assert.Equal("9", Cfg["Scanning:ManualCooldownMinutes"]);
    }

    [DbFact]
    public async Task One_bad_value_changes_nothing_and_names_the_setting()
    {
        await Clean();
        var owner = Factory.As(Owner);
        var response = await Put(owner, new() { ["Scanning:ManualCooldownMinutes"] = "3", ["Alerts:Email:Port"] = "not a port" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("invalid_number", json.GetProperty("error").GetString());
        Assert.Equal("Alerts:Email:Port", json.GetProperty("key").GetString());
        Assert.Empty(await Rows());

        Assert.Equal(HttpStatusCode.BadRequest, (await Put(owner, new() { ["Nope:Key"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(owner, new() { ["ConnectionStrings:Default"] = "Host=evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(owner, new() { ["Alerts:Email:Password"] = "" })).StatusCode);   // clearing a secret is a reset, not an empty value
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(owner, reset: ["Nope:Key"])).StatusCode);
        Assert.Empty(await Rows());
    }

    [DbFact]
    public async Task The_list_says_where_each_value_comes_from()
    {
        await Clean();
        await Put(Factory.As(Owner), new() { ["Alerts:Email:Host"] = "smtp.example.test" });
        using var scope = Scope();
        var views = await scope.ServiceProvider.GetRequiredService<AppSettingsService>().ListAsync();
        Assert.Equal(AppSettingsCatalog.All.Count, views.Count);
        Assert.Equal((SettingSource.Database, "smtp.example.test"), (views.Single(v => v.Def.Key == "Alerts:Email:Host").Source, views.Single(v => v.Def.Key == "Alerts:Email:Host").Value));
        var env = views.Single(v => v.Def.Key == "Scanning:ManualCooldownMinutes");
        Assert.Equal((SettingSource.Environment, "9"), (env.Source, env.Value));
        var untouched = views.Single(v => v.Def.Key == "Alerts:Email:Port");
        Assert.Equal((SettingSource.Default, "587"), (untouched.Source, untouched.Value));
        await Clean();
    }

    [DbFact]
    public async Task The_settings_page_is_for_the_platform_owner_and_never_shows_a_secret()
    {
        await Clean();
        await Put(Factory.As(Owner), new() { ["Alerts:Email:Password"] = "hunter2-secret", ["Alerts:Email:Host"] = "smtp.example.test" });
        var org = await NewOrg();
        Assert.Equal(HttpStatusCode.Forbidden, (await Factory.As(org.Admin).GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.As(null).GetAsync("/settings")).StatusCode);

        var page = await (await Factory.As(Owner).GetAsync("/settings")).Content.ReadAsStringAsync();
        Assert.Contains("smtp.example.test", page);               // ordinary values are shown ...
        Assert.DoesNotContain("hunter2-secret", page);            // ... secrets never
        Assert.Contains("Server settings", page);
        await Clean();
    }

    [DbFact]
    public async Task The_chosen_theme_reaches_every_page_and_changes_nothing_by_default()
    {
        await Clean();
        Assert.Contains("data-theme=\"default\"", await (await Factory.As(null).GetAsync("/login")).Content.ReadAsStringAsync());
        await Put(Factory.As(Owner), new() { ["Appearance:Theme"] = "christmas" });
        Assert.Contains("data-theme=\"christmas\"", await (await Factory.As(null).GetAsync("/login")).Content.ReadAsStringAsync());
        await Clean();
    }
}
