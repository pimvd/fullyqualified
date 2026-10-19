using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public static class Roles
{
    public const string Admin = nameof(Role.Admin);
    public const string Edit = "Edit";
    /// <summary>An admin of the management tenant: may see and manage every tenant.</summary>
    public const string Platform = "Platform";
    public const string TenantClaim = "tid";
    public const string ManagementClaim = "mgmt";

    public static bool CanEdit(ClaimsPrincipal? u) => u is not null && (u.IsInRole(nameof(Role.Member)) || u.IsInRole(nameof(Role.Admin)));
    public static bool IsAdmin(ClaimsPrincipal? u) => u?.IsInRole(nameof(Role.Admin)) == true;
    public static bool IsPlatformAdmin(ClaimsPrincipal? u) => IsAdmin(u) && u!.HasClaim(ManagementClaim, "1");
    public static Guid? UserId(ClaimsPrincipal? u) => Guid.TryParse(u?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public static Guid? TenantId(ClaimsPrincipal? u) => Guid.TryParse(u?.FindFirstValue(TenantClaim), out var id) ? id : null;
}

/// <summary>What the session cookie claims, kept in step with the database.</summary>
public record SessionUser(Guid Id, string Name, Role Role, Guid TenantId, bool IsManagement);

/// <summary>Builds the session cookie and keeps it honest: roles, tenants and removals take effect without waiting for the cookie to expire.</summary>
public class Session(IMemoryCache cache, AppDbContext db)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public static ClaimsPrincipal Principal(SessionUser u)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()), new(ClaimTypes.Name, u.Name), new(ClaimTypes.Role, u.Role.ToString()),
            new(Roles.TenantClaim, u.TenantId.ToString()),
        };
        if (u.IsManagement) claims.Add(new Claim(Roles.ManagementClaim, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>Signs in and records the login time.</summary>
    public async Task SignIn(HttpContext ctx, User user)
    {
        // Sign-in runs before a tenant is known, so these lookups are deliberately across tenants.
        var tenant = await db.AcrossTenants<Tenant>().SingleAsync(t => t.Id == user.TenantId);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await ctx.SignInAsync(Scheme, Principal(new SessionUser(user.Id, user.Name, user.Role, user.TenantId, tenant.IsManagement)),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Call after changing or removing a user so this server process stops trusting the old cookie at once.</summary>
    public void Forget(Guid userId) => cache.Remove(Key(userId));

    static string Key(Guid id) => "session:" + id;

    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        if (Roles.UserId(ctx.Principal) is not { } id) { await Reject(ctx); return; }
        var current = await ctx.HttpContext.RequestServices.GetRequiredService<Session>().Lookup(id);
        if (current is null) { await Reject(ctx); return; }
        var p = ctx.Principal!;
        if (!p.IsInRole(current.Role.ToString()) || p.Identity?.Name != current.Name || Roles.TenantId(p) != current.TenantId
            || p.HasClaim(Roles.ManagementClaim, "1") != current.IsManagement)
        {
            ctx.ReplacePrincipal(Principal(current));
            ctx.ShouldRenew = true;
        }
    }

    static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(Scheme);
    }

    async Task<SessionUser?> Lookup(Guid id) => await cache.GetOrCreateAsync(Key(id), async e =>
    {
        e.AbsoluteExpirationRelativeToNow = CacheFor;
        // The cookie is validated before the request has a tenant, so look across tenants.
        return await (from u in db.AcrossTenants<User>().AsNoTracking()
                      join t in db.AcrossTenants<Tenant>().AsNoTracking() on u.TenantId equals t.Id
                      where u.Id == id
                      select new SessionUser(u.Id, u.Name, u.Role, u.TenantId, t.IsManagement)).SingleOrDefaultAsync();
    });
}
