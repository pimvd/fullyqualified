using System.Net;
using System.Text.RegularExpressions;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class BulkDeleteTests(PgFactory factory) : IClassFixture<PgFactory>
{
    record Ids(Guid Admin, Guid Viewer, Guid[] Mine, Guid Theirs);

    IServiceScope Scope()
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        return scope;
    }

    /// <summary>A tenant with an admin, a viewer and three assets, and another tenant with one asset.</summary>
    async Task<Ids> Seed()
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var mine = new Tenant { Name = "Bulk " + Guid.NewGuid().ToString("N")[..6] };
        var other = new Tenant { Name = "Other " + Guid.NewGuid().ToString("N")[..6] };
        db.Tenants.AddRange(mine, other);
        var admin = new User { TenantId = mine.Id, Name = "Bea", Role = Role.Admin, Email = $"bea@{mine.Id:N}.test" };
        var viewer = new User { TenantId = mine.Id, Name = "Vic", Role = Role.Viewer, Email = $"vic@{mine.Id:N}.test" };
        db.Users.AddRange(admin, viewer);
        var assets = Enumerable.Range(0, 3).Select(i => new Asset { TenantId = mine.Id, Name = $"a{i}.{mine.Id:N}.test", Kind = AssetKind.Endpoint, Discovered = i > 0 }).ToArray();
        var theirs = new Asset { TenantId = other.Id, Name = $"theirs.{other.Id:N}.test", Kind = AssetKind.Domain };
        db.Assets.AddRange(assets);
        db.Assets.Add(theirs);
        db.Results.Add(new CheckResult { TenantId = mine.Id, AssetId = assets[0].Id, CheckType = "tls", Status = CheckStatus.Ok, Summary = "Sum_DnsOk" });
        db.Jobs.Add(new Job { TenantId = mine.Id, AssetId = assets[1].Id, CheckType = "dns" });
        await db.SaveChangesAsync();
        return new Ids(admin.Id, viewer.Id, assets.Select(a => a.Id).ToArray(), theirs.Id);
    }

    async Task<HttpResponseMessage> PostDelete(HttpClient client, IEnumerable<Guid> ids)
    {
        var page = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("_handler", "bulk-delete"), new("__RequestVerificationToken", token) };
        form.AddRange(ids.Select(i => new KeyValuePair<string, string>("ids", i.ToString())));
        return await client.PostAsync("/", new FormUrlEncodedContent(form));
    }

    [DbFact]
    public async Task The_table_offers_checkboxes_to_editors_only()
    {
        var ids = await Seed();
        var admin = await (await factory.As(ids.Admin).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains($"name=\"ids\" value=\"{ids.Mine[0]}\"", admin);
        Assert.Contains("Delete selected", admin);
        var viewer = await (await factory.As(ids.Viewer).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("name=\"ids\"", viewer);
        Assert.DoesNotContain("Delete selected", viewer);
    }

    [DbFact]
    public async Task An_editor_deletes_the_selected_assets_with_their_history_and_nothing_else()
    {
        var ids = await Seed();
        // The list includes an asset of another tenant, which must survive.
        var response = await PostDelete(factory.As(ids.Admin), [ids.Mine[0], ids.Mine[1], ids.Theirs]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("deleted=2", response.Headers.Location!.OriginalString);

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var left = await db.Assets.Select(a => a.Id).ToListAsync();
        Assert.DoesNotContain(ids.Mine[0], left);
        Assert.DoesNotContain(ids.Mine[1], left);
        Assert.Contains(ids.Mine[2], left);
        Assert.Contains(ids.Theirs, left);
        Assert.False(await db.Results.AnyAsync(r => r.AssetId == ids.Mine[0]));   // history goes with the asset
        Assert.False(await db.Jobs.AnyAsync(j => j.AssetId == ids.Mine[1]));      // and so do queued checks

        var page = await (await factory.As(ids.Admin).GetAsync("/?deleted=2")).Content.ReadAsStringAsync();
        Assert.Contains("Deleted 2 asset(s).", page);
    }

    [DbFact]
    public async Task A_viewer_cannot_delete_and_an_empty_selection_deletes_nothing()
    {
        var ids = await Seed();
        await PostDelete(factory.As(ids.Viewer), ids.Mine);
        var none = await PostDelete(factory.As(ids.Admin), []);
        Assert.Contains("deleted=0", none.Headers.Location!.OriginalString);

        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.Assets.CountAsync(a => ids.Mine.Contains(a.Id)));
        var page = await (await factory.As(ids.Admin).GetAsync("/?deleted=0")).Content.ReadAsStringAsync();
        Assert.Contains("Select at least one asset first.", page);
    }
}
