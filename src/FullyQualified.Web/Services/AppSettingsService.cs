using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public record SettingsRequest(Dictionary<string, string?>? Set, string[]? Reset);

/// <summary>Where the effective value of a setting comes from.</summary>
public enum SettingSource { Database, Environment, Default }

/// <summary>A setting as the page shows it. A secret never carries its value, only whether one is set.</summary>
public record SettingView(SettingDef Def, SettingSource Source, string? Value, bool IsSet, DateTimeOffset? UpdatedAt, string? UpdatedBy);

/// <summary>Reads and writes the server settings in the database. Only the platform owner reaches it.</summary>
public class AppSettingsService(AppDbContext db, DbSettings settings, IConfiguration cfg, IDataProtectionProvider dp, ILogger<AppSettingsService> log)
{
    static IResult Fail(int status, string code, string? key = null) => Results.Json(new { error = code, key }, statusCode: status);

    public async Task<List<SettingView>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.AppSettings.AsNoTracking().ToDictionaryAsync(r => r.Key, StringComparer.OrdinalIgnoreCase, ct);
        var views = new List<SettingView>();
        foreach (var def in AppSettingsCatalog.All)
        {
            rows.TryGetValue(def.Key, out var row);
            var source = row is not null ? SettingSource.Database : settings.SetOutsideDatabase(cfg, def.Key) ? SettingSource.Environment : SettingSource.Default;
            var secret = def.Kind == SettingKind.Secret;
            var value = secret ? null : source == SettingSource.Default ? def.Default : cfg[def.Key] ?? "";
            views.Add(new SettingView(def, source, value, secret ? source != SettingSource.Default : !string.IsNullOrEmpty(value), row?.UpdatedAt, row?.UpdatedBy));
        }
        return views;
    }

    public async Task<IResult> Save(HttpContext ctx, SettingsRequest r)
    {
        var set = r.Set ?? [];
        var reset = r.Reset ?? [];
        // Validate everything first, so a bad value changes nothing.
        foreach (var (key, value) in set)
        {
            var def = AppSettingsCatalog.Find(key);
            if (def is null) return Fail(400, "unknown_setting", key);
            if (value is null || (def.Kind == SettingKind.Secret && value.Length == 0)) return Fail(400, "invalid_value", key);
            if (AppSettingsCatalog.Validate(def, value) is { } error) return Fail(400, error, key);
        }
        foreach (var key in reset) if (AppSettingsCatalog.Find(key) is null) return Fail(400, "unknown_setting", key);

        var protector = dp.CreateProtector(DbSettings.Purpose);
        var by = ctx.User.Identity?.Name;
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in set)
        {
            var def = AppSettingsCatalog.Find(key)!;
            var secret = def.Kind == SettingKind.Secret;
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == def.Key, ctx.RequestAborted);
            if (row is null) db.AppSettings.Add(row = new AppSetting { Key = def.Key });
            row.Value = secret ? protector.Protect(value!) : value!.Trim();
            row.IsSecret = secret;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
        foreach (var key in reset)
        {
            var def = AppSettingsCatalog.Find(key)!;
            await db.AppSettings.Where(x => x.Key == def.Key).ExecuteDeleteAsync(ctx.RequestAborted);
        }
        await db.SaveChangesAsync(ctx.RequestAborted);
        settings.Refresh();                       // this process follows at once; the others within the poll interval
        log.LogWarning("Server settings changed by {By}: set {Set}, reset {Reset}", by, string.Join(", ", set.Keys), string.Join(", ", reset));
        return Results.NoContent();
    }
}

public static class AppSettingsEndpoints
{
    public static void MapAppSettings(this WebApplication app) =>
        app.MapGroup("/platform/settings").RequireAuthorization(Roles.Platform)
            .MapPut("/", (HttpContext c, AppSettingsService s, SettingsRequest r) => s.Save(c, r));
}
