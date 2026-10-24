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
builder.Services.AddSingleton<DigService>();
builder.Services.AddToolbox();
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
builder.Services.AddScoped<TenantScope>();
builder.Services.AddScoped<DbGate>();
builder.Services.AddScoped<TenantStats>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<PlanAdminService>();
builder.Services.AddScoped<SettingsTestService>();
builder.Services.AddSingleton<ServerProbes>();
builder.Services.AddSingleton<ScanThrottle>();
builder.Services.AddScoped<PublicScanService>();
builder.Services.AddScoped<LogQueries>();
builder.Services.AddScoped<AlertSettingsService>();
builder.Services.AddScoped<SsoService>();
builder.Services.AddMemoryCache();
builder.Services.AddEmailTests();
builder.Services.AddHostedService<JobRunner>();

// Accounts sign in with passkeys only. Keys live in the database so cookies survive restarts and redeploys.
builder.Services.AddDataProtection().SetApplicationName("FullyQualified").PersistKeysToDbContext<AppDbContext>();
static bool IsApi(HttpRequest r) => r.Path.StartsWithSegments("/auth") || r.Path.StartsWithSegments("/admin") || r.Path.StartsWithSegments("/api/toolbox");

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
    .AddPolicy(Roles.Platform, p => p.RequireRole(nameof(Role.Admin)).RequireClaim(Roles.ManagementClaim, "1"))
    .AddPolicy(Roles.Edit, p => p.RequireRole(nameof(Role.Member), nameof(Role.Admin)));

var app = builder.Build();

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Operator command: dotnet FullyQualified.Web.dll recover <e-mail or name> [--url https://host]
if (args is ["recover", var who, ..])
{
    var urlAt = Array.IndexOf(args, "--url");
    var baseUrl = urlAt >= 0 && urlAt + 1 < args.Length ? args[urlAt + 1] : app.Configuration["Auth:Origin"] ?? "http://localhost:8080";
    return await OperatorCommands.RecoverAsync(app.Services, who, baseUrl, Console.Out);
}

// Files first, then routing: the catch-all "page not found" route would otherwise match /app.css before the file is served.
app.Use(async (ctx, next) =>
{
    if (ToolboxEndpoints.IsPrivatePage(ctx.Request.Path))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next(ctx);
});
app.UseStaticFiles();
app.UseRouting();

var cultures = new[] { new CultureInfo("en"), new CultureInfo("nl") };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
});

app.UseAuthentication();
// From here on the request works for the tenant in the session. Without one, queries on tenant data return nothing.
app.Use((ctx, next) =>
{
    if (Roles.TenantId(ctx.User) is { } tenant) ctx.RequestServices.GetRequiredService<TenantScope>().Id = tenant;
    return next();
});
app.UseAuthorization();
app.UseRateLimiter();
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

// Reports are part of some plans only; others are sent back to the Reports page, which explains it.
static async Task<bool> ReportsAllowed(HttpContext ctx, PlanService plans) =>
    Roles.TenantId(ctx.User) is { } tenant && (await plans.ForAsync(tenant))?.Limits.ReportsEnabled == true;

app.MapGet("/reports/nis2.xlsx", async (HttpContext ctx, PlanService plans, ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    !await ReportsAllowed(ctx, plans) ? Results.Redirect("/reports") :
    Results.File(ReportWriter.Excel(await reports.BuildAsync(), l),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.xlsx"));

app.MapGet("/reports/nis2.pdf", async (HttpContext ctx, PlanService plans, ReportBuilder reports, IStringLocalizer<SharedResource> l) =>
    !await ReportsAllowed(ctx, plans) ? Results.Redirect("/reports") :
    Results.File(ReportWriter.Pdf(await reports.BuildAsync(), l), "application/pdf", $"nis2-report-{DateTime.UtcNow:yyyyMMdd}.pdf"));

app.MapAuth();
// Live DNS lookup for the overlay. Any signed-in user; the fallback policy already requires that.
app.MapGet("/api/dig", async (HttpContext ctx, DigService dig, string? name, string? type, string? resolver) =>
{
    var who = Roles.UserId(ctx.User)?.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (!dig.Allow(who)) return Results.Json(new { error = "rate" }, statusCode: 429);
    var n = DigService.NormalizeName(name);
    if (n is null) return Results.Json(new { error = "invalid_name" }, statusCode: 400);
    var t = (type ?? "A").ToUpperInvariant();
    if (!DigService.Types.Contains(t)) return Results.Json(new { error = "invalid_type" }, statusCode: 400);
    if (System.Net.IPAddress.TryParse(n, out _) && t != "PTR") t = "PTR";
    var results = await dig.DigAsync(n, t, resolver ?? "all", ctx.RequestAborted);
    return results is null ? Results.Json(new { error = "invalid_resolver" }, statusCode: 400) : Results.Json(new { name = n, type = t, results });
});

app.MapAssetControls();
app.MapReview();
app.MapAdmin();
app.MapAlerts();
app.MapEmailTests();
app.MapToolbox();
app.MapPlans();
app.MapDiagnostics();
app.MapSso();
app.MapRazorComponents<App>();

app.Run();
return 0;

public partial class Program;
