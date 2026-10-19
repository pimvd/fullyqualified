using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FullyQualified.Web;
using FullyQualified.Web.Components;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres"));
builder.Services.AddRazorComponents();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("ct", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FullyQualified/0.1 (+https://github.com/pimvd/fullyqualified)");
});

builder.Services.AddSingleton<HostGuard>();
builder.Services.AddSingleton<ICheck, TlsCheck>();
builder.Services.AddSingleton<ICheck, DnsCheck>();
builder.Services.AddSingleton<ICheck, CtLogCheck>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<CheckRunner>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<ReportBuilder>();
builder.Services.AddHostedService<JobRunner>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Optional shared password for self-hosted installs (user name is ignored). /health stays open.
if (app.Configuration["Auth:Password"] is { Length: > 0 } password)
{
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/health") { await next(); return; }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var given = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var supplied = given[(given.IndexOf(':') + 1)..];
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), expected)) { await next(); return; }
            }
            catch (FormatException) { }
        }
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"FullyQualified\"";
        ctx.Response.StatusCode = 401;
    });
}

var cultures = new[] { new CultureInfo("en"), new CultureInfo("nl") };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/culture", (string culture, string? redirect, HttpContext ctx) =>
{
    if (cultures.Any(c => c.Name == culture))
        ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), SameSite = SameSiteMode.Lax });
    var target = redirect is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//") ? r : "/";
    return Results.LocalRedirect(target);
});

app.MapGet("/reports/nis2.xlsx", async (ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    Results.File(ReportWriter.Excel(await reports.BuildAsync(), l),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.xlsx"));

app.MapGet("/reports/nis2.pdf", async (ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    Results.File(ReportWriter.Pdf(await reports.BuildAsync(), l), "application/pdf", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.pdf"));

app.MapRazorComponents<App>();

app.Run();

public partial class Program;
