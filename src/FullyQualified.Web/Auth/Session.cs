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

    public static bool CanEdit(ClaimsPrincipal? u) => u is not null && (u.IsInRole(nameof(Role.Member)) || u.IsInRole(nameof(Role.Admin)));
    public static bool IsAdmin(ClaimsPrincipal? u) => u?.IsInRole(nameof(Role.Admin)) == true;
    public static Guid? UserId(ClaimsPrincipal? u) => Guid.TryParse(u?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

/// <summary>Builds the session cookie and keeps it honest: roles and removals take effect without waiting for the cookie to expire.</summary>
public class Session(IMemoryCache cache, AppDbContext db)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role.ToString())],
        Scheme));

    public static Task SignIn(HttpContext ctx, User user) =>
        ctx.SignInAsync(Scheme, Principal(user), new AuthenticationProperties { IsPersistent = true });

    /// <summary>Call after changing or removing a user so this server process stops trusting the old cookie at once.</summary>
    public void Forget(Guid userId) => cache.Remove(Key(userId));

    static string Key(Guid id) => "session:" + id;

    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        if (Roles.UserId(ctx.Principal) is not { } id) { await Reject(ctx); return; }
        var current = await ctx.HttpContext.RequestServices.GetRequiredService<Session>().Lookup(id);
        if (current is null) { await Reject(ctx); return; }
        if (!ctx.Principal!.IsInRole(current.Role.ToString()) || ctx.Principal.Identity?.Name != current.Name)
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

    async Task<User?> Lookup(Guid id) => await cache.GetOrCreateAsync(Key(id), async e =>
    {
        e.AbsoluteExpirationRelativeToNow = CacheFor;
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
    });
}
