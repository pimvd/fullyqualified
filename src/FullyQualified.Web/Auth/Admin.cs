using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

public record InviteRequest(string? Email, string? Role, Guid? TenantId);
public record RoleRequest(string? Role);
public record TenantRequest(string? Name, string? AdminEmail, string? Tier);

/// <summary>Invitations, recovery links and people management. Tenant admins act within their tenant; admins of the management tenant act on any tenant.</summary>
public class AdminService(AppDbContext db, Session session, PlanService plans, AlertService mail, IStringLocalizer<SharedResource> l, ILogger<AdminService> log)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(24);

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        return e.Length <= 254 && MailAddress.TryCreate(e, out var m) && m.Address == e && e.Contains('@') ? e : "";
    }

    static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>A user the caller may manage: in their own tenant, or any tenant for platform admins. Others look like they do not exist.</summary>
    async Task<User?> Manageable(HttpContext ctx, Guid id)
    {
        var user = await db.AcrossTenants<User>().SingleOrDefaultAsync(u => u.Id == id);
        return user is not null && (user.TenantId == Roles.TenantId(ctx.User) || Roles.IsPlatformAdmin(ctx.User)) ? user : null;
    }

    async Task<bool> Mail(string to, string subject, string body, CancellationToken ct)
    {
        if (!mail.CanMail) return false;
        try { await mail.MailAsync(to, subject, body, ct); return true; }
        catch (Exception ex) { log.LogWarning(ex, "Could not send e-mail"); return false; }
    }

    // ---- invitations ----

    public async Task<IResult> CreateInvite(HttpContext ctx, InviteRequest req)
    {
        if (!Enum.TryParse<Role>(req.Role, out var role)) return Fail(400, "invalid_role");
        var email = NormalizeEmail(req.Email);
        if (email == "") return Fail(400, "invalid_email");
        var own = Roles.TenantId(ctx.User)!.Value;
        var target = req.TenantId ?? own;
        if (target != own)
        {
            if (!Roles.IsPlatformAdmin(ctx.User)) return Fail(403, "forbidden");
            if (!await db.Tenants.AnyAsync(t => t.Id == target)) return Fail(404, "not_found");
            db.PlatformWrite = true;
        }
        if (email is not null && await db.AcrossTenants<User>().AnyAsync(u => u.Email == email)) return Fail(409, "email_exists");
        if (await plans.ForAsync(target) is { } plan && !plan.HasRoomForUser()) return Fail(409, "user_limit");

        var (token, invite) = Issue(ctx, target, role, email, null, InviteLifetime);
        db.Invites.Add(invite);
        await db.SaveChangesAsync();

        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/invite/{token}";
        var emailed = email is not null && await Mail(email, l["Mail_InviteSubject"].Value,
            l["Mail_InviteBody", ctx.User.Identity?.Name ?? "", url, (int)InviteLifetime.TotalDays].Value, ctx.RequestAborted);
        return Results.Ok(new { url, emailed });
    }

    (string Token, Invite Invite) Issue(HttpContext ctx, Guid tenant, Role role, string? email, Guid? recoveryFor, TimeSpan lifetime)
    {
        var token = NewToken();
        return (token, new Invite
        {
            TenantId = tenant, TokenHash = PasskeyService.Hash(token), Email = email, Role = role, RecoveryForUser = recoveryFor,
            CreatedBy = Roles.UserId(ctx.User)!.Value, ExpiresAt = DateTimeOffset.UtcNow + lifetime,
        });
    }

    public async Task<IResult> RevokeInvite(HttpContext ctx, Guid id)
    {
        var invite = await db.AcrossTenants<Invite>().SingleOrDefaultAsync(i => i.Id == id && i.UsedAt == null);
        if (invite is null || (invite.TenantId != Roles.TenantId(ctx.User) && !Roles.IsPlatformAdmin(ctx.User))) return Fail(404, "not_found");
        db.Invites.Remove(invite);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- people ----

    public async Task<IResult> ChangeRole(HttpContext ctx, Guid id, RoleRequest req)
    {
        if (!Enum.TryParse<Role>(req.Role, out var role)) return Fail(400, "invalid_role");
        var user = await Manageable(ctx, id);
        if (user is null) return Fail(404, "not_found");
        if (user.Role == Role.Admin && role != Role.Admin && !await OtherAdmin(user)) return Fail(409, "last_admin");
        db.PlatformWrite = true;
        user.Role = role;
        await db.SaveChangesAsync();
        session.Forget(id);
        return Results.NoContent();
    }

    public async Task<IResult> RemoveUser(HttpContext ctx, Guid id)
    {
        if (Roles.UserId(ctx.User) == id) return Fail(409, "cannot_remove_self");
        var user = await Manageable(ctx, id);
        if (user is null) return Fail(404, "not_found");
        if (user.Role == Role.Admin && !await OtherAdmin(user)) return Fail(409, "last_admin");
        db.PlatformWrite = true;
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        session.Forget(id);
        return Results.NoContent();
    }

    /// <summary>Every tenant keeps at least one admin.</summary>
    Task<bool> OtherAdmin(User user) => db.AcrossTenants<User>().AnyAsync(u => u.TenantId == user.TenantId && u.Role == Role.Admin && u.Id != user.Id);

    /// <summary>For a person who lost their passkey: a single-use link, valid for a day, that sets up a new one and revokes the old ones.</summary>
    public async Task<IResult> ResetPasskey(HttpContext ctx, Guid id)
    {
        var user = await Manageable(ctx, id);
        if (user is null) return Fail(404, "not_found");
        db.PlatformWrite = true;
        // Earlier unused recovery links for this person stop working.
        db.Invites.RemoveRange(await db.AcrossTenants<Invite>().Where(i => i.RecoveryForUser == id && i.UsedAt == null).ToListAsync());
        var (token, invite) = Issue(ctx, user.TenantId, user.Role, null, id, RecoveryLifetime);
        db.Invites.Add(invite);
        await db.SaveChangesAsync();

        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/recover/{token}";
        var emailed = user.Email is not null && await Mail(user.Email, l["Mail_RecoverySubject"].Value,
            l["Mail_RecoveryBody", ctx.User.Identity?.Name ?? "", url, (int)RecoveryLifetime.TotalHours].Value, ctx.RequestAborted);
        return Results.Ok(new { url, emailed });
    }

    // ---- platform: tenants ----

    public async Task<IResult> CreateTenant(HttpContext ctx, TenantRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Fail(400, "invalid_name");
        var email = NormalizeEmail(req.AdminEmail);
        if (email == "") return Fail(400, "invalid_email");
        if (await db.Tenants.AnyAsync(t => t.Name == name)) return Fail(409, "tenant_exists");
        if (email is not null && await db.AcrossTenants<User>().AnyAsync(u => u.Email == email)) return Fail(409, "email_exists");

        var tier = string.IsNullOrWhiteSpace(req.Tier) ? Tiers.Free : req.Tier.Trim().ToLowerInvariant();
        if (!Tiers.Keys.Contains(tier)) return Fail(400, "invalid_tier");
        db.PlatformWrite = true;
        var tenant = new Tenant { Name = name, TierKey = tier };
        db.Tenants.Add(tenant);
        var (token, invite) = Issue(ctx, tenant.Id, Role.Admin, email, null, InviteLifetime);
        db.Invites.Add(invite);
        await db.SaveChangesAsync();

        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/invite/{token}";
        var emailed = email is not null && await Mail(email, l["Mail_InviteSubject"].Value,
            l["Mail_InviteBody", ctx.User.Identity?.Name ?? "", url, (int)InviteLifetime.TotalDays].Value, ctx.RequestAborted);
        return Results.Ok(new { tenantId = tenant.Id, url, emailed });
    }
}

public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/admin").RequireAuthorization(Roles.Admin);
        g.MapPost("/invites", (HttpContext c, AdminService s, InviteRequest r) => s.CreateInvite(c, r));
        g.MapDelete("/invites/{id:guid}", (HttpContext c, AdminService s, Guid id) => s.RevokeInvite(c, id));
        g.MapPut("/users/{id:guid}/role", (HttpContext c, AdminService s, Guid id, RoleRequest r) => s.ChangeRole(c, id, r));
        g.MapDelete("/users/{id:guid}", (HttpContext c, AdminService s, Guid id) => s.RemoveUser(c, id));
        g.MapPost("/users/{id:guid}/reset-passkey", (HttpContext c, AdminService s, Guid id) => s.ResetPasskey(c, id));

        app.MapGroup("/platform").RequireAuthorization(Roles.Platform)
            .MapPost("/tenants", (HttpContext c, AdminService s, TenantRequest r) => s.CreateTenant(c, r));
    }
}
