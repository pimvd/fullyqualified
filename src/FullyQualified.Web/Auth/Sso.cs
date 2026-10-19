using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>Decides whether an e-mail address asserted by the identity provider may be used to link or create an account.</summary>
public static class SsoPolicy
{
    public static string[] Domains(string? configured) =>
        (configured ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.TrimStart('@').ToLowerInvariant()).ToArray();

    /// <summary>With allowed domains set, only those count. Without them the provider must say the address is verified.</summary>
    public static bool Trusted(string email, string? emailVerified, string[] allowedDomains)
    {
        var at = email.LastIndexOf('@');
        if (at < 1) return false;
        if (allowedDomains.Length > 0) return allowedDomains.Contains(email[(at + 1)..].ToLowerInvariant());
        return string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase);
    }
}

public class SsoService(AppDbContext db, IConfiguration cfg)
{
    public const string Provider = "oidc";
    public const string ExternalScheme = "fq_external";
    /// <summary>The handler drops the "iss" claim, so the validated token issuer is copied into this one.</summary>
    public const string IssuerClaim = "fq_issuer";

    public static bool Enabled(IConfiguration cfg) =>
        !string.IsNullOrWhiteSpace(cfg["Sso:Authority"]) && !string.IsNullOrWhiteSpace(cfg["Sso:ClientId"]);

    /// <summary>Returns the user to sign in, or the reason the identity may not enter.</summary>
    public async Task<(User? User, string? Error)> Resolve(ClaimsPrincipal external)
    {
        var issuer = external.FindFirstValue(IssuerClaim);
        var subject = external.FindFirstValue("sub");
        if (issuer is null || subject is null) return (null, "sso_failed");
        var email = AdminService.NormalizeEmail(external.FindFirstValue("email"));
        if (email == "") email = null;
        var name = external.FindFirstValue("name") ?? external.FindFirstValue("preferred_username") ?? email ?? subject;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(7001)");

        // Signing in happens before a tenant is known, so identity lookups are across tenants.
        if (await db.AcrossTenants<ExternalLogin>().SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject) is { } known)
            return (await db.AcrossTenants<User>().SingleAsync(u => u.Id == known.UserId), null);

        // First account is created through /setup with a passkey, so an SSO sign-in can never claim an empty instance.
        if (!await db.AcrossTenants<User>().AnyAsync()) return (null, "sso_no_account");
        if (email is null) return (null, "sso_no_email");
        if (!SsoPolicy.Trusted(email, external.FindFirstValue("email_verified"), SsoPolicy.Domains(cfg["Sso:AllowedDomains"])))
            return (null, "sso_domain");

        var user = await db.AcrossTenants<User>().SingleOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            var invite = await db.AcrossTenants<Invite>().Where(i => i.Email == email && i.RecoveryForUser == null && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow).OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync();
            if (invite is not null)
            {
                invite.UsedAt = DateTimeOffset.UtcNow;
                user = new User { TenantId = invite.TenantId, Name = name, Email = email, Role = invite.Role };
            }
            else if (cfg.GetValue("Sso:AutoProvision", false))
            {
                var role = Enum.TryParse<Role>(cfg["Sso:DefaultRole"], out var r) && r != Role.Admin ? r : Role.Viewer;
                // Without an invitation the account joins the management tenant, which is the only tenant of a self-hosted install.
                var management = await db.Tenants.SingleAsync(t => t.IsManagement);
                user = new User { TenantId = management.Id, Name = name, Email = email, Role = role };
            }
            else return (null, "sso_no_account");
            db.Users.Add(user);
        }
        db.ExternalLogins.Add(new ExternalLogin { TenantId = user.TenantId, UserId = user.Id, Issuer = issuer, Subject = subject });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (user, null);
    }
}

public static class SsoEndpoints
{
    public static void MapSso(this WebApplication app)
    {
        if (!SsoService.Enabled(app.Configuration)) return;
        var g = app.MapGroup("/auth/sso").AllowAnonymous();
        g.MapGet("/start", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/auth/sso/complete" }, [SsoService.Provider]));
        g.MapGet("/complete", async (HttpContext ctx, SsoService sso, Session session) =>
        {
            var external = await ctx.AuthenticateAsync(SsoService.ExternalScheme);
            await ctx.SignOutAsync(SsoService.ExternalScheme);
            if (!external.Succeeded)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Sso").LogWarning(external.Failure, "Single sign-on did not complete");
                return Results.LocalRedirect("/login?error=sso_failed");
            }
            var (user, error) = await sso.Resolve(external.Principal!);
            if (user is null) return Results.LocalRedirect($"/login?error={error}");
            await session.SignIn(ctx, user);
            return Results.LocalRedirect("/");
        });
    }

    public static void AddSso(this AuthenticationBuilder auth, IConfiguration cfg)
    {
        if (!SsoService.Enabled(cfg)) return;
        auth.AddCookie(SsoService.ExternalScheme, o =>
        {
            o.Cookie.Name = "fq_external";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        auth.AddOpenIdConnect(SsoService.Provider, cfg["Sso:DisplayName"] ?? "SSO", o =>
        {
            o.Authority = cfg["Sso:Authority"];
            o.ClientId = cfg["Sso:ClientId"];
            o.ClientSecret = cfg["Sso:ClientSecret"];
            o.RequireHttpsMetadata = cfg.GetValue("Sso:RequireHttpsMetadata", true);
            o.ResponseType = "code";
            // A query response is a top-level GET, so Lax cookies are enough and this also works over plain http in development.
            o.ResponseMode = "query";
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.UsePkce = true;
            o.SignInScheme = SsoService.ExternalScheme;
            o.CallbackPath = "/auth/sso/signin-oidc";
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Events.OnTokenValidated = ctx =>
            {
                ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(SsoService.IssuerClaim, ctx.SecurityToken.Issuer));
                return Task.CompletedTask;
            };
            o.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "email" }) o.Scope.Add(scope);
        });
    }
}
