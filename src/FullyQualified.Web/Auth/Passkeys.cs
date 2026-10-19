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

public record RegisterRequest(string? Name, string? InviteToken, string? SetupCode);
public record LabelRequest(string? Label);

/// <summary>Challenge state kept between the options and the completion call, in a short-lived protected cookie.</summary>
record Pending(string OptionsJson, string Name, Guid UserId, Guid? InviteId);

public class PasskeyService(AppDbContext db, IDataProtectionProvider dp, IConfiguration cfg)
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
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Fail(400, "invalid_name");

        Guid? inviteId = null;
        if (!await db.Users.AnyAsync())
        {
            var code = cfg["Auth:SetupCode"];
            if (!string.IsNullOrEmpty(code) && !CryptographicOperations.FixedTimeEquals(Hash(req.SetupCode ?? ""), Hash(code)))
                return Fail(403, "invalid_setup_code");
        }
        else
        {
            inviteId = (await FindInvite(req.InviteToken))?.Id;
            if (inviteId is null) return Fail(403, "invalid_invite");
        }

        var userId = Guid.NewGuid();
        var options = Fido(ctx).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.ToByteArray(), Name = name, DisplayName = name },
            ExcludeCredentials = [],
            AuthenticatorSelection = Selection,
            AttestationPreference = AttestationConveyancePreference.None,
        });
        SaveState(ctx, new Pending(options.ToJson(), name, userId, inviteId));
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
                    !await db.Passkeys.IgnoreQueryFilters().AnyAsync(p => p.CredentialId == args.CredentialId, ct),
            });
        }
        catch (Fido2VerificationException) { return Fail(400, "verification_failed"); }

        await using var tx = await db.Database.BeginTransactionAsync();
        // Serializes concurrent first-user setups and invite redemptions.
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(7001)");

        User user;
        if (addToExisting)
            user = await db.Users.SingleAsync(u => u.Id == state.UserId);
        else
        {
            // The first account is the administrator; everyone after that joins through an invitation with a role.
            var role = Role.Admin;
            string? email = null;
            if (await db.Users.AnyAsync())
            {
                var invite = state.InviteId is { } id ? await db.Invites.SingleOrDefaultAsync(i => i.Id == id && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow) : null;
                if (invite is null) return Fail(403, "invalid_invite");
                invite.UsedAt = DateTimeOffset.UtcNow;
                role = invite.Role;
                email = invite.Email;
            }
            user = new User { Id = state.UserId, Name = state.Name, Role = role, Email = email };
            db.Users.Add(user);
        }

        db.Passkeys.Add(new Passkey
        {
            UserId = user.Id, CredentialId = credential.Id, PublicKey = credential.PublicKey, SignCount = credential.SignCount,
            Name = addToExisting ? state.Name : "Passkey",
        });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Fail(409, "email_exists"); }
        await tx.CommitAsync();

        if (!addToExisting) await Session.SignIn(ctx, user);
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
        var passkey = await db.Passkeys.SingleOrDefaultAsync(p => p.CredentialId == response.RawId);
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
        await Session.SignIn(ctx, await db.Users.SingleAsync(u => u.Id == passkey.UserId));
        return Results.Ok(new { redirect = "/" });
    }

    // ---- invitations are created by administrators, see AdminEndpoints ----

    public async Task<Invite?> FindInvite(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        return await db.Invites.SingleOrDefaultAsync(i => i.TokenHash == hash && i.UsedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow);
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
