using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Tests;

public class KeyLocalizer : IStringLocalizer<FullyQualified.Web.SharedResource>
{
    public LocalizedString this[string name] => new(name, name);
    public LocalizedString this[string name, params object[] arguments] => new(name, name);
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}

public class NoDbFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Database:AutoMigrate", "false");
        builder.UseSetting("Scanning:Enabled", "false");
    }
}
