using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FullyQualified.Web;

/// <summary>
/// Reads the <c>app_settings</c> table into configuration (ADR 0013), so everything that already reads <c>IConfiguration</c> sees database values without change.
/// It is added after every other source, so the database wins over the environment and appsettings.json. Only keys from <see cref="AppSettingsCatalog"/> count.
/// The table is polled, so the web and worker containers follow a change within seconds. Secrets are decrypted once <see cref="Attach"/> has been called;
/// until then they are simply not set. No database, or no table yet (a fresh install before the migration), is not an error: the provider keeps what it has.
/// </summary>
public sealed class DbConfigurationProvider(string connection, TimeSpan every) : ConfigurationProvider, IDisposable
{
    readonly object gate = new();
    Timer? timer;
    Func<string, string?>? unprotect;

    public override void Load()
    {
        Refresh();
        timer ??= new Timer(_ => Refresh(), null, every, every);
    }

    /// <summary>From now on secrets are decrypted with this.</summary>
    public void Attach(Func<string, string?> decrypt)
    {
        unprotect = decrypt;
        Refresh();
    }

    /// <summary>Reads the table now and tells the configuration when something changed.</summary>
    public void Refresh()
    {
        lock (gate)
        {
            Dictionary<string, string?> fresh;
            try { fresh = Read(); }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.IO.IOException or TimeoutException) { return; }
            if (fresh.Count == Data.Count && fresh.All(kv => Data.TryGetValue(kv.Key, out var old) && old == kv.Value)) return;
            Data = fresh;
            OnReload();
        }
    }

    Dictionary<string, string?> Read()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var db = new NpgsqlConnection(connection);
        db.Open();
        using var cmd = new NpgsqlCommand("""select "Key", "Value", "IsSecret" from app_settings""", db);
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var def = AppSettingsCatalog.Find(rows.GetString(0));
            if (def is null) continue;                                             // a stray row never overrides anything
            var value = rows.IsDBNull(1) ? "" : rows.GetString(1);
            if (rows.GetBoolean(2))
            {
                if (unprotect is null) continue;
                try { value = unprotect(value) ?? ""; }
                catch (System.Security.Cryptography.CryptographicException) { continue; }
            }
            result[def.Key] = value;
        }
        return result;
    }

    public void Dispose() => timer?.Dispose();
}

public sealed class DbConfigurationSource(string connection, TimeSpan every) : IConfigurationSource
{
    public DbConfigurationProvider? Provider { get; private set; }
    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider = new DbConfigurationProvider(connection, every);
}

/// <summary>The handle the rest of the app uses for the database-backed configuration: add it, attach data protection, refresh after a change.</summary>
public sealed class DbSettings(DbConfigurationProvider provider)
{
    public const string Purpose = "fq.settings";
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);

    /// <summary>Adds the database source last, so it overrides the environment. Call it right after the builder is created.</summary>
    public static DbSettings Add(ConfigurationManager cfg, TimeSpan? every = null)
    {
        var cs = new NpgsqlConnectionStringBuilder(Hosting.ConnectionString(cfg)) { Timeout = 3, CommandTimeout = 3 }.ConnectionString;
        var source = new DbConfigurationSource(cs, every ?? PollEvery);
        ((IConfigurationBuilder)cfg).Add(source);
        return new DbSettings(source.Provider!);
    }

    /// <summary>Call when data protection is available (after the host is built), so stored secrets can be read.</summary>
    public void Attach(IDataProtectionProvider dp)
    {
        var protector = dp.CreateProtector(Purpose);
        provider.Attach(protector.Unprotect);
    }

    public void Refresh() => provider.Refresh();

    /// <summary>True when something other than the database sets the key (environment variable, appsettings.json, command line).</summary>
    public bool SetOutsideDatabase(IConfiguration cfg, string key) =>
        cfg is IConfigurationRoot root && root.Providers.Any(p => p is not DbConfigurationProvider && p.TryGet(key, out var v) && !string.IsNullOrEmpty(v));
}
