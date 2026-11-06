using System.Net;
using System.Net.Http.Json;
using FullyQualified.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullyQualified.Tests;

/// <summary>After the first setup, anyone can register a new organization. The passkey ceremony itself is not run here.</summary>
public class RegisterTests(PgFactory factory) : IClassFixture<PgFactory>
{
    async Task SeedFirstUser()
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().All = true;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        if (await db.Tenants.AnyAsync()) return;
        var mgmt = new Tenant { Name = "Management", IsManagement = true };
        db.Tenants.Add(mgmt);
        db.Users.Add(new User { TenantId = mgmt.Id, Name = "Mia", Role = Role.Admin, Email = "mia@mgmt.test" });
        await db.SaveChangesAsync();
    }

    Task<HttpResponseMessage> Options(string? org, string? name = "Sam", string? email = "sam@new.test") =>
        factory.As(null).PostAsJsonAsync("/auth/register/options", new RegisterRequest(name, null, null, org, email));

    [DbFact]
    public async Task A_new_organization_can_register_without_an_invitation()
    {
        await SeedFirstUser();
        Assert.Equal(HttpStatusCode.OK, (await Options("New Org")).StatusCode);
    }

    [DbFact]
    public async Task Registration_needs_an_organization_name_and_a_valid_email()
    {
        await SeedFirstUser();
        Assert.Equal(HttpStatusCode.BadRequest, (await Options(null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Options("Org", email: "nope")).StatusCode);
    }

    [DbFact]
    public async Task An_existing_organization_name_or_email_is_refused()
    {
        await SeedFirstUser();
        Assert.Equal(HttpStatusCode.Conflict, (await Options("Management")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Options("Other Org", email: "mia@mgmt.test")).StatusCode);
    }

    [DbFact]
    public async Task The_register_page_is_public()
    {
        await SeedFirstUser();
        var page = await factory.As(null).GetAsync("/register");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
