using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FullyQualified.Web;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<CheckResult> Results => Set<CheckResult>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<CertificateRecord> Certificates => Set<CertificateRecord>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Passkey> Passkeys => Set<Passkey>();
    public DbSet<Invite> Invites => Set<Invite>();
    /// <summary>Keeps the cookie and challenge keys across restarts. Infrastructure, so it has no tenant id.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(260);
        });
        b.Entity<CheckResult>(e =>
        {
            e.ToTable("check_results");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => new { x.AssetId, x.CheckType, x.ObservedAt });
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ValuesJson).HasColumnType("jsonb");
        });
        b.Entity<Change>(e =>
        {
            e.ToTable("changes");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => x.DetectedAt);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CertificateRecord>(e =>
        {
            e.ToTable("certificates");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => new { x.AssetId, x.Source });
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.Property(x => x.Name).HasMaxLength(100);
        });
        b.Entity<Passkey>(e =>
        {
            e.ToTable("passkeys");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => x.CredentialId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Invite>(e =>
        {
            e.ToTable("invites");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasQueryFilter(x => x.TenantId == Tenant.Default);
            e.HasIndex(x => new { x.State, x.RunAt });
            e.HasIndex(x => new { x.AssetId, x.CheckType }).IsUnique();
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Used by `dotnet ef` at design time.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres").Options);
}
