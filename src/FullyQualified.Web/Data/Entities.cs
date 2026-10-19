using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace FullyQualified.Web;

/// <summary>A customer organization. The first tenant ever created is the management tenant: its admins see and manage all tenants.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsManagement { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Everything a tenant owns carries its id. An unset id (empty) is stamped on save from the current tenant, or the save is refused.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public enum AssetKind { Domain, Endpoint, CloudResource }

/// <summary>Ordered by severity so the numeric value can be used to find the worst status.</summary>
public enum CheckStatus { Ok = 0, Warning = 1, Error = 2, Critical = 3 }

public class Asset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public AssetKind Kind { get; set; }
    public required string Name { get; set; }
    public bool Discovered { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CheckResult : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public required string CheckType { get; set; }
    public CheckStatus Status { get; set; }
    /// <summary>Resource key, optionally followed by '|'-separated arguments.</summary>
    public required string Summary { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public Dictionary<string, string> Values =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(ValuesJson) ?? [];
}

public class Change : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public required string CheckType { get; set; }
    public required string Key { get; set; }
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CertificateRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    /// <summary>"endpoint" or "ct".</summary>
    public required string Source { get; set; }
    public required string Host { get; set; }
    public required string Subject { get; set; }
    public required string Issuer { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public string Renewal { get; set; } = "Manual";
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public int DaysLeft => (int)Math.Floor((NotAfter - DateTimeOffset.UtcNow).TotalDays);
}

public class Job : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public required string CheckType { get; set; }
    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedAt { get; set; }
    public string State { get; set; } = "pending";
}

/// <summary>Viewer reads, Member also manages assets, Admin also manages people. Each role includes the ones before it.</summary>
public enum Role { Viewer = 0, Member = 1, Admin = 2 }

public class User : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    /// <summary>Lower case. Set from an invitation or from single sign-on; used to link an SSO identity to this user.</summary>
    public string? Email { get; set; }
    public Role Role { get; set; } = Role.Viewer;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>A WebAuthn credential. Accounts have no passwords; a user signs in with any of their passkeys.</summary>
public class Passkey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required byte[] CredentialId { get; set; }
    public required byte[] PublicKey { get; set; }
    public uint SignCount { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Single-use link that lets a new person register their own account. Only a hash of the token is stored.</summary>
public class Invite : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required byte[] TokenHash { get; set; }
    /// <summary>Optional. When set, the same person can also accept the invitation by signing in with SSO using this address.</summary>
    public string? Email { get; set; }
    public Role Role { get; set; } = Role.Viewer;
    /// <summary>Set for a passkey recovery link: the existing user who gets a new passkey. Null for a normal invitation.</summary>
    public Guid? RecoveryForUser { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Links a user to an identity at the single sign-on provider (issuer + subject), so the link survives e-mail changes.</summary>
public class ExternalLogin : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Where a tenant sends alarms. The URLs are stored encrypted with Data Protection.</summary>
public class AlertSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; } = true;
    public string? SlackWebhook { get; set; }
    public string? WebhookUrl { get; set; }
    /// <summary>Comma-separated recipients.</summary>
    public string? EmailTo { get; set; }
    /// <summary>Alert only when a check reaches at least this status.</summary>
    public CheckStatus MinStatus { get; set; } = CheckStatus.Warning;
    public bool NotifyRecovery { get; set; } = true;
    public bool NotifyDrift { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One delivery attempt to one channel; feeds the log screen.</summary>
public class AlertLogEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required string Channel { get; set; }
    public required string Subject { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
