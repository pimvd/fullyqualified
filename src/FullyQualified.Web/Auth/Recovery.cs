using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>Commands for whoever runs the server, for the case where nobody who could issue a recovery link can sign in any more.</summary>
public static class OperatorCommands
{
    /// <summary>Prints a single-use link (valid 24 hours) that sets up a new passkey for one user and revokes the old ones.</summary>
    public static async Task<int> RecoverAsync(IServiceProvider services, string who, string baseUrl, TextWriter output)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = who.Trim();
        var email = AdminService.NormalizeEmail(key);

        var matches = await db.AcrossTenants<User>().Where(u => u.Email == email || u.Name == key).ToListAsync();
        if (matches.Count == 0) { await output.WriteLineAsync($"No user matches '{key}'."); return 1; }
        if (matches.Count > 1)
        {
            await output.WriteLineAsync($"'{key}' matches {matches.Count} users; use the e-mail address:");
            foreach (var m in matches) await output.WriteLineAsync($"  {m.Name} <{m.Email ?? "no e-mail"}>");
            return 1;
        }

        var user = matches[0];
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        db.PlatformWrite = true;
        db.Invites.RemoveRange(await db.AcrossTenants<Invite>().Where(i => i.RecoveryForUser == user.Id && i.UsedAt == null).ToListAsync());
        db.Invites.Add(new Invite
        {
            TenantId = user.TenantId, TokenHash = PasskeyService.Hash(token), Role = user.Role, RecoveryForUser = user.Id,
            CreatedBy = user.Id, ExpiresAt = DateTimeOffset.UtcNow + AdminService.RecoveryLifetime,
        });
        await db.SaveChangesAsync();
        await output.WriteLineAsync($"Recovery link for {user.Name} (valid {(int)AdminService.RecoveryLifetime.TotalHours} hours, single use):");
        await output.WriteLineAsync($"{baseUrl.TrimEnd('/')}/recover/{token}");
        return 0;
    }
}
