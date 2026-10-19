using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

public record InviteRequest(string? Email, string? Role);
public record RoleRequest(string? Role);

/// <summary>Invitations and people management. Everything here requires the Admin role.</summary>
public class AdminService(AppDbContext db, Session session, AlertService mail, IStringLocalizer<SharedResource> l, ILogger<AdminService> log)
{
    internal static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    public static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        return e.Length <= 254 && MailAddress.TryCreate(e, out var m) && m.Address == e && e.Contains('@') ? e : "";
    }

    public async Task<IResult> CreateInvite(HttpContext ctx, InviteRequest req)
    {
        if (!Enum.TryParse<Role>(req.Role, out var role)) return Fail(400, "invalid_role");
        var email = NormalizeEmail(req.Email);
        if (email == "") return Fail(400, "invalid_email");
        if (email is not null && await db.Users.AnyAsync(u => u.Email == email)) return Fail(409, "email_exists");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var inviter = Roles.UserId(ctx.User)!.Value;
        db.Invites.Add(new Invite
        {
            TokenHash = PasskeyService.Hash(token), Email = email, Role = role, CreatedBy = inviter,
            ExpiresAt = DateTimeOffset.UtcNow + InviteLifetime,
        });
        await db.SaveChangesAsync();

        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/invite/{token}";
        var emailed = false;
        if (email is not null && mail.CanMail)
        {
            try
            {
                await mail.MailAsync(email, l["Mail_InviteSubject"].Value,
                    l["Mail_InviteBody", ctx.User.Identity?.Name ?? "", url, (int)InviteLifetime.TotalDays].Value, ctx.RequestAborted);
                emailed = true;
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not e-mail the invitation"); }
        }
        return Results.Ok(new { url, emailed });
    }

    public async Task<IResult> RevokeInvite(Guid id)
    {
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Id == id && i.UsedAt == null);
        if (invite is null) return Fail(404, "not_found");
        db.Invites.Remove(invite);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    public async Task<IResult> ChangeRole(Guid id, RoleRequest req)
    {
        if (!Enum.TryParse<Role>(req.Role, out var role)) return Fail(400, "invalid_role");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return Fail(404, "not_found");
        if (user.Role == Role.Admin && role != Role.Admin && !await OtherAdmin(id)) return Fail(409, "last_admin");
        user.Role = role;
        await db.SaveChangesAsync();
        session.Forget(id);
        return Results.NoContent();
    }

    public async Task<IResult> RemoveUser(HttpContext ctx, Guid id)
    {
        if (Roles.UserId(ctx.User) == id) return Fail(409, "cannot_remove_self");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return Fail(404, "not_found");
        if (user.Role == Role.Admin && !await OtherAdmin(id)) return Fail(409, "last_admin");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        session.Forget(id);
        return Results.NoContent();
    }

    Task<bool> OtherAdmin(Guid id) => db.Users.AnyAsync(u => u.Role == Role.Admin && u.Id != id);
}

public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/admin").RequireAuthorization(Roles.Admin);
        g.MapPost("/invites", (HttpContext c, AdminService s, InviteRequest r) => s.CreateInvite(c, r));
        g.MapDelete("/invites/{id:guid}", (AdminService s, Guid id) => s.RevokeInvite(id));
        g.MapPut("/users/{id:guid}/role", (AdminService s, Guid id, RoleRequest r) => s.ChangeRole(id, r));
        g.MapDelete("/users/{id:guid}", (HttpContext c, AdminService s, Guid id) => s.RemoveUser(c, id));
    }
}
