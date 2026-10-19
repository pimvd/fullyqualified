using System.Globalization;
using FullyQualified.Web;
using FullyQualified.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
builder.Services.AddScoped<PasskeyService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<SsoService>();
builder.Services.AddMemoryCache();
builder.Services.AddHostedService<JobRunner>();

// Accounts sign in with passkeys only. Keys live in the database so cookies survive restarts and redeploys.
builder.Services.AddDataProtection().SetApplicationName("FullyQualified").PersistKeysToDbContext<AppDbContext>();
static bool IsApi(HttpRequest r) => r.Path.StartsWithSegments("/auth") || r.Path.StartsWithSegments("/admin");

var auth = builder.Services.AddAuthentication(Session.Scheme);
auth.AddCookie(o =>
{
    o.Cookie.Name = "fq_auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/forbidden";
    o.Events.OnValidatePrincipal = Session.Validate;
    o.Events.OnRedirectToLogin = ctx =>
    {
        // Scripts get a status code; browsers get sent to the login page.
        if (IsApi(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (IsApi(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});
auth.AddSso(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Roles.Admin, p => p.RequireRole(nameof(Role.Admin)))
    .AddPolicy(Roles.Edit, p => p.RequireRole(nameof(Role.Member), nameof(Role.Admin)));

var app = builder.Build();

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

var cultures = new[] { new CultureInfo("en"), new CultureInfo("nl") };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
});

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapGet("/culture", (string culture, string? redirect, HttpContext ctx) =>
{
    if (cultures.Any(c => c.Name == culture))
        ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), SameSite = SameSiteMode.Lax });
    var target = redirect is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//") ? r : "/";
    return Results.LocalRedirect(target);
}).AllowAnonymous();

app.MapGet("/reports/nis2.xlsx", async (ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    Results.File(ReportWriter.Excel(await reports.BuildAsync(), l),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.xlsx"));

app.MapGet("/reports/nis2.pdf", async (ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    Results.File(ReportWriter.Pdf(await reports.BuildAsync(), l), "application/pdf", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.pdf"));

app.MapAuth();
app.MapAdmin();
app.MapSso();
app.MapRazorComponents<App>();

app.Run();

public partial class Program;
