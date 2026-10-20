using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FullyQualified.Web;

/// <summary>Which tenant the current request or job works for. Fails closed: with no id and no <see cref="All"/>, queries see nothing.</summary>
public class TenantScope
{
    public Guid Id { get; set; }
    /// <summary>Set only in background services, which work for every tenant. Never set it in a request.</summary>
    public bool All { get; set; }
}

/// <summary>Blazor starts sibling components' async initialisation at the same time, but a DbContext allows one operation at once. Components take turns through this.</summary>
public class DbGate
{
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task RunAsync(Func<Task> work)
    {
        await gate.WaitAsync();
        try { await work(); }
        finally { gate.Release(); }
    }
}

public class AppDbContext(DbContextOptions<AppDbContext> options, TenantScope scope) : DbContext(options), IDataProtectionKeyContext
{
    Guid CurrentTenantId => scope.Id;
    bool AllTenants => scope.All;

    /// <summary>Deliberately ignores the tenant filter. Use it only for sign-in, invitations and platform administration, and say why at the call site.</summary>
    public IQueryable<T> AcrossTenants<T>() where T : class => Set<T>().IgnoreQueryFilters();

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Tier> Tiers => Set<Tier>();
    public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();
    public DbSet<AlertLogEntry> AlertLog => Set<AlertLogEntry>();

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<CheckResult> Results => Set<CheckResult>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<CertificateRecord> Certificates => Set<CertificateRecord>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Passkey> Passkeys => Set<Passkey>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    /// <summary>Keeps the cookie and challenge keys across restarts. Infrastructure, so it has no tenant id.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenants();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenants();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>New rows get the current tenant; a row without any tenant, or for another tenant, is refused rather than saved somewhere wrong.</summary>
    void EnforceTenants()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var owned = entry.Entity;
            if (entry.State == EntityState.Added && owned.TenantId == Guid.Empty) owned.TenantId = CurrentTenantId;
            if (owned.TenantId == Guid.Empty)
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} has no tenant");
            if (!AllTenants && CurrentTenantId != Guid.Empty && owned.TenantId != CurrentTenantId && !PlatformWrite)
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} belongs to another tenant");
        }
    }

    /// <summary>Platform administration creates rows for other tenants; it opts in per operation.</summary>
    public bool PlatformWrite { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(260);
        });
        b.Entity<CheckResult>(e =>
        {
            e.ToTable("check_results");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.AssetId, x.CheckType, x.ObservedAt });
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ValuesJson).HasColumnType("jsonb");
        });
        b.Entity<Change>(e =>
        {
            e.ToTable("changes");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => x.DetectedAt);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CertificateRecord>(e =>
        {
            e.ToTable("certificates");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.AssetId, x.Source });
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsManagement).IsUnique().HasFilter("\"IsManagement\""); // exactly one management tenant
        });
        b.Entity<Tier>(e =>
        {
            e.ToTable("tiers");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(40);
        });
        b.Entity<Tenant>().HasOne<Tier>().WithMany().HasForeignKey(x => x.TierKey).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Tenant>().Property(x => x.TierKey).HasMaxLength(20);
        b.Entity<AlertSettings>(e =>
        {
            e.ToTable("alert_settings");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => x.TenantId).IsUnique();
            e.Property(x => x.MinStatus).HasConversion<string>().HasMaxLength(10);
        });
        b.Entity<AlertLogEntry>(e =>
        {
            e.ToTable("alert_log");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.TenantId, x.At });
            e.Property(x => x.Channel).HasMaxLength(20);
        });
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.Email).IsUnique(); // one account per address keeps SSO matching unambiguous
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(10);
        });
        b.Entity<Passkey>(e =>
        {
            e.ToTable("passkeys");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => x.CredentialId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Invite>(e =>
        {
            e.ToTable("invites");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(10);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExternalLogin>(e =>
        {
            e.ToTable("external_logins");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
            e.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasQueryFilter(x => AllTenants || x.TenantId == CurrentTenantId);
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
            .UseNpgsql("Host=localhost;Database=fullyqualified;Username=postgres;Password=postgres").Options, new TenantScope());
}
