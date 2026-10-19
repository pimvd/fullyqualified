using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace FullyQualified.Web;

public static class Tenant
{
    /// <summary>Self-hosted installs use a single tenant; every table still carries a tenant id.</summary>
    public static readonly Guid Default = Guid.Empty;
}

public enum AssetKind { Domain, Endpoint, CloudResource }

/// <summary>Ordered by severity so the numeric value can be used to find the worst status.</summary>
public enum CheckStatus { Ok = 0, Warning = 1, Error = 2, Critical = 3 }

public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Tenant.Default;
    public AssetKind Kind { get; set; }
    public required string Name { get; set; }
    public bool Discovered { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CheckResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Tenant.Default;
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

public class Change
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Tenant.Default;
    public Guid AssetId { get; set; }
    public required string CheckType { get; set; }
    public required string Key { get; set; }
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CertificateRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Tenant.Default;
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

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Tenant.Default;
    public Guid AssetId { get; set; }
    public required string CheckType { get; set; }
    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedAt { get; set; }
    public string State { get; set; } = "pending";
}
