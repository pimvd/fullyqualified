using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace FullyQualified.Web;

/// <summary>A customer organization. The first tenant ever created is the management tenant: its admins see and manage all tenants.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsManagement { get; set; }
    /// <summary>The plan this tenant is on; see <see cref="Tier"/>.</summary>
    public string TierKey { get; set; } = Tiers.Free;
    /// <summary>
    /// Per-tenant overrides of the tier. Null follows the tier; for the numbers, -1 means unlimited (or, for retention, keep forever).
    /// </summary>
    public int? OverrideMaxDomains { get; set; }
    public int? OverrideMaxEndpoints { get; set; }
    public int? OverrideMaxUsers { get; set; }
    public bool? OverrideReports { get; set; }
    public int? OverrideRetentionMonths { get; set; }
    /// <summary>Minutes between checks. Null follows the tier; -1 means the server default.</summary>
    public int? OverrideScanIntervalMinutes { get; set; }
    /// <summary>When someone last pressed "check everything now"; used for the cooldown.</summary>
    public DateTimeOffset? LastManualRunAt { get; set; }
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

/// <summary>
/// A plan with its limits. Free, Starter and Business are the plans of the product plan; Custom is for agreements made per customer and
/// is normally combined with per-tenant overrides. The management owner edits the values. A null number means unlimited.
/// </summary>
public class Tier
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public int? MaxDomains { get; set; }
    public int? MaxEndpoints { get; set; }
    public int? MaxUsers { get; set; }
    public bool ReportsEnabled { get; set; }
    /// <summary>How many months of results, changes and alarm history are kept. Null keeps everything.</summary>
    public int? RetentionMonths { get; set; }
    /// <summary>Minutes between checks of an asset. Null uses the server default (<c>Scanning:IntervalHours</c>).</summary>
    public int? ScanIntervalMinutes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Tiers
{
    public const string Free = "free", Starter = "starter", Business = "business", Custom = "custom";
    public static readonly string[] Keys = [Free, Starter, Business, Custom];

    /// <summary>The values a fresh install starts with. Domains and history follow docs/PLAN.md; the rest are starting points to adjust.</summary>
    public static readonly Tier[] Defaults =
    [
        new() { Key = Free, Name = "Free", SortOrder = 1, MaxDomains = 3, MaxEndpoints = 10, MaxUsers = 2, ReportsEnabled = false, RetentionMonths = 1, ScanIntervalMinutes = 1440 },
        new() { Key = Starter, Name = "Starter", SortOrder = 2, MaxDomains = 25, MaxEndpoints = 100, MaxUsers = 10, ReportsEnabled = true, RetentionMonths = 12, ScanIntervalMinutes = 360 },
        new() { Key = Business, Name = "Business", SortOrder = 3, MaxDomains = 100, MaxEndpoints = 500, MaxUsers = 50, ReportsEnabled = true, RetentionMonths = 24, ScanIntervalMinutes = 60 },
        new() { Key = Custom, Name = "Custom", SortOrder = 4, MaxDomains = null, MaxEndpoints = null, MaxUsers = null, ReportsEnabled = true, RetentionMonths = null, ScanIntervalMinutes = null },
    ];
}
