using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record RegisterRequest(string? Name, string? InviteToken, string? SetupCode, string? Organization, string? Email = null);
public record LabelRequest(string? Label);

/// <summary>Challenge state kept between the options and the completion call, in a short-lived protected cookie.</summary>
record Pending(string OptionsJson, string Name, Guid UserId, Guid? InviteId, string? Organization = null, bool Recovery = false, string? Email = null);

public class PasskeyService(AppDbContext db, Session session, PlanService plans, IDataProtectionProvider dp, IConfiguration cfg)
{
    const string StateCookie = "fq_pk";
    readonly ITimeLimitedDataProtector protector = dp.CreateProtector("fq.passkey.state").ToTimeLimitedDataProtector();

    Fido2 Fido(HttpContext ctx)
    {
        var host = ctx.Request.Host;
        var origin = cfg["Auth:Origin"] is { Length: > 0 } o ? o : $"{ctx.Request.Scheme}://{host}";
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = cfg["Auth:RpId"] is { Length: > 0 } id ? id : host.Host,
            ServerName = "FullyQualified",
            Origins = new HashSet<string> { origin },
        });
    }

    static Guid? CurrentUser(HttpContext ctx) =>
        Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    internal static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    void SaveState(HttpContext ctx, object state) =>
        ctx.Response.Cookies.Append(StateCookie, protector.Protect(JsonSerializer.Serialize(state), TimeSpan.FromMinutes(5)),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5) });

    T? TakeState<T>(HttpContext ctx)
    {
        var raw = ctx.Request.Cookies[StateCookie];
        ctx.Response.Cookies.Delete(StateCookie);
        if (raw is null) return default;
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(raw)); }
        catch (CryptographicException) { return default; }
    }

    static IResult Fail(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    static AuthenticatorSelection Selection => new()
    {
        ResidentKey = ResidentKeyRequirement.Required,
        UserVerification = UserVerificationRequirement.Preferred,
    };

    // ---- registration: first user (setup), invited user, and an existing user adding a passkey ----

    public async Task<IResult> RegisterOptions(HttpContext ctx, RegisterRequest req)
    {
        // Registration starts before anyone is signed in, so these lookups are across tenants by design.
        Guid? inviteId = null;
        string name;
        Guid userId;
        string? organization = null;
        string? email = null;
        var recovery = false;
        List<PublicKeyCredentialDescriptor> exclude = [];

        if (!await db.AcrossTenants<User>().AnyAsync())
        {
            name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) return Fail(400, "invalid_name");
            organization = req.Organization?.Trim() is { Length: > 0 } o ? o : "Management";
            if (organization.Length > 100) return Fail(400, "invalid_name");
            email = AdminService.NormalizeEmail(req.Email);
            if (string.IsNullOrEmpty(email)) return Fail(400, "invalid_email");
            var code = cfg["Auth:SetupCode"];
            if (!string.IsNullOrEmpty(code) && !CryptographicOperations.FixedTimeEquals(Hash(req.SetupCode ?? ""), Hash(code)))
                return Fail(403, "invalid_setup_code");
            userId = Guid.NewGuid();
        }
        else
        {
            var invite = await FindInvite(req.InviteToken, recovery: null);
            if (invite is null) return Fail(403, "invalid_invite");
            inviteId = invite.Id;
            if (invite.RecoveryForUser is { } target)
            {
                // A recovery link sets up a new passkey for an existing user, whose old passkeys are replaced.
                recovery = true;
                userId = target;
                name = (await db.AcrossTenants<User>().SingleAsync(u => u.Id == target)).Name;
            }
            else
            {
                name = req.Name?.Trim() ?? "";
                if (name.Length is 0 or > 100) return Fail(400, "invalid_name");
                if (string.IsNullOrEmpty(invite.Email)) return Fail(400, "invalid_email"); // an invitation from before e-mail was mandatory
                userId = Guid.NewGuid();
            }
        }

        var options = Fido(ctx).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.ToByteArray(), Name = name, DisplayName = name },
            ExcludeCredentials = exclude,
            AuthenticatorSelection = Selection,
            AttestationPreference = AttestationConveyancePreference.None,
        });
        SaveState(ctx, new Pending(options.ToJson(), name, userId, inviteId, organization, recovery, email));
        return Results.Content(options.ToJson(), "application/json");
    }

    public async Task<IResult> AddOptions(HttpContext ctx, LabelRequest req)
    {
        var userId = CurrentUser(ctx)!.Value;
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        var existing = await db.Passkeys.Where(p => p.UserId == userId).Select(p => p.CredentialId).ToListAsync();
        var options = Fido(ctx).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.ToByteArray(), Name = user.Name, DisplayName = user.Name },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = Selection,
            AttestationPreference = AttestationConveyancePreference.None,
        });
        SaveState(ctx, new Pending(options.ToJson(), Label(req.Label), userId, null));
        return Results.Content(options.ToJson(), "application/json");
    }

    static string Label(string? label) => label?.Trim() is { Length: > 0 and <= 100 } l ? l : "Passkey";

    public async Task<IResult> Complete(HttpContext ctx, AuthenticatorAttestationRawResponse response, bool addToExisting)
    {
        var state = TakeState<Pending>(ctx);
        if (state is null) return Fail(400, "expired");
        var signedIn = CurrentUser(ctx);
        if (addToExisting && signedIn != state.UserId) return Fail(403, "forbidden");

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await Fido(ctx).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(state.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await db.AcrossTenants<Passkey>().AnyAsync(p => p.CredentialId == args.CredentialId, ct),
            });
        }
        catch (Fido2VerificationException) { return Fail(400, "verification_failed"); }

        await using var tx = await db.Database.BeginTransactionAsync();
        // Serializes concurrent first-user setups and invite redemptions.
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(7001)");

        User user;
        if (addToExisting)
            user = await db.Users.SingleAsync(u => u.Id == state.UserId);
        else if (!await db.AcrossTenants<User>().AnyAsync())
        {
            // The first account creates the first tenant, which becomes the management tenant.
            var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.IsManagement); // an upgrade may already have created it for existing data
            if (tenant is null) db.Tenants.Add(tenant = new Tenant { Name = state.Organization ?? "Management", IsManagement = true, TierKey = Tiers.Custom });
            user = new User { Id = state.UserId, TenantId = tenant.Id, Name = state.Name, Role = Role.Admin, Email = state.Email };
            db.Users.Add(user);
        }
        else
        {
            var invite = state.InviteId is { } id
                ? await db.AcrossTenants<Invite>().SingleOrDefaultAsync(i => i.Id == id && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow)
                : null;
            if (invite is null) return Fail(403, "invalid_invite");
            invite.UsedAt = DateTimeOffset.UtcNow;
            if (state.Recovery)
            {
                if (invite.RecoveryForUser != state.UserId) return Fail(403, "invalid_invite");
                user = await db.AcrossTenants<User>().SingleAsync(u => u.Id == state.UserId);
                // Whatever passkeys the person still had are revoked: the point of recovery is that the old ones are gone or untrusted.
                db.Passkeys.RemoveRange(await db.AcrossTenants<Passkey>().Where(p => p.UserId == user.Id).ToListAsync());
            }
            else
            {
                if (await plans.ForAsync(invite.TenantId) is { } plan && !plan.HasRoomForUser(redeemingInvite: true)) return Fail(409, "user_limit");
                user = new User { Id = state.UserId, TenantId = invite.TenantId, Name = state.Name, Role = invite.Role, Email = invite.Email };
                db.Users.Add(user);
            }
        }

        db.Passkeys.Add(new Passkey
        {
            TenantId = user.TenantId, UserId = user.Id, CredentialId = credential.Id, PublicKey = credential.PublicKey, SignCount = credential.SignCount,
            Name = addToExisting ? state.Name : "Passkey",
        });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Fail(409, "email_exists"); }
        await tx.CommitAsync();

        if (!addToExisting) await session.SignIn(ctx, user);
        return Results.Ok(new { redirect = "/" });
    }

    public async Task<IResult> RemovePasskey(HttpContext ctx, Guid id)
    {
        var userId = CurrentUser(ctx)!.Value;
        var own = await db.Passkeys.Where(p => p.UserId == userId).ToListAsync();
        if (own.Count <= 1) return Fail(409, "last_passkey");
        var target = own.SingleOrDefault(p => p.Id == id);
        if (target is null) return Fail(404, "not_found");
        db.Passkeys.Remove(target);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- sign in with a discoverable credential, so no user name is asked ----

    public IResult LoginOptions(HttpContext ctx)
    {
        var options = Fido(ctx).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });
        SaveState(ctx, new Pending(options.ToJson(), "", Guid.Empty, null));
        return Results.Content(options.ToJson(), "application/json");
    }

    public async Task<IResult> LoginComplete(HttpContext ctx, AuthenticatorAssertionRawResponse response)
    {
        var state = TakeState<Pending>(ctx);
        if (state is null) return Fail(400, "expired");
        var passkey = await db.AcrossTenants<Passkey>().SingleOrDefaultAsync(p => p.CredentialId == response.RawId);
        if (passkey is null) return Fail(401, "unknown_passkey");

        VerifyAssertionResult result;
        try
        {
            result = await Fido(ctx).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(state.OptionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(passkey.UserId.ToByteArray())),
            });
        }
        catch (Fido2VerificationException) { return Fail(401, "verification_failed"); }

        passkey.SignCount = result.SignCount;
        passkey.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await session.SignIn(ctx, await db.AcrossTenants<User>().SingleAsync(u => u.Id == passkey.UserId));
        return Results.Ok(new { redirect = "/" });
    }

    // ---- invitations are created by administrators, see AdminEndpoints ----

    /// <param name="recovery">true: only recovery links, false: only invitations, null: either.</param>
    public async Task<Invite?> FindInvite(string? token, bool? recovery)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        var invite = await db.AcrossTenants<Invite>().SingleOrDefaultAsync(i => i.TokenHash == hash && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow);
        return invite is null || (recovery is { } r && r != (invite.RecoveryForUser is not null)) ? null : invite;
    }
}

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var anon = app.MapGroup("/auth").AllowAnonymous();
        anon.MapPost("/register/options", (HttpContext c, PasskeyService s, RegisterRequest r) => s.RegisterOptions(c, r));
        anon.MapPost("/register/complete", (HttpContext c, PasskeyService s, AuthenticatorAttestationRawResponse r) => s.Complete(c, r, false));
        anon.MapPost("/login/options", (HttpContext c, PasskeyService s) => s.LoginOptions(c));
        anon.MapPost("/login/complete", (HttpContext c, PasskeyService s, AuthenticatorAssertionRawResponse r) => s.LoginComplete(c, r));

        var authed = app.MapGroup("/auth");
        authed.MapPost("/passkeys/options", (HttpContext c, PasskeyService s, LabelRequest r) => s.AddOptions(c, r));
        authed.MapPost("/passkeys/complete", (HttpContext c, PasskeyService s, AuthenticatorAttestationRawResponse r) => s.Complete(c, r, true));
        authed.MapDelete("/passkeys/{id:guid}", (HttpContext c, PasskeyService s, Guid id) => s.RemovePasskey(c, id));
        authed.MapPost("/logout", async (HttpContext c) =>
        {
            await c.SignOutAsync(Session.Scheme);
            return Results.LocalRedirect("/login");
        });
    }
}
