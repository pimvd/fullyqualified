using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <summary>Pausing assets, muting findings and certificates. Plain form posts that redirect back, so they work without scripts.</summary>
public static partial class AssetControlEndpoints
{
    static readonly string[] CheckTypes = ["tls", "dns", "ct"];

    [GeneratedRegex(@"^[A-Za-z0-9:_.*-]{1,120}$")]
    private static partial Regex ValidCode();

    static string? Clean(string? reason) => reason?.Trim() is { Length: > 0 } r ? (r.Length > 200 ? r[..200] : r) : null;

    /// <summary>"yyyy-MM-dd" in the future becomes the start of that day (UTC); empty means no end date. False when invalid or not in the future.</summary>
    static bool TryEnd(string? until, out DateTimeOffset? end)
    {
        end = null;
        if (string.IsNullOrWhiteSpace(until)) return true;
        if (!DateOnly.TryParseExact(until.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day <= DateOnly.FromDateTime(DateTime.UtcNow)) return false;
        end = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return true;
    }

    /// <summary>Only a path on this site: never somewhere else.</summary>
    static string Back(string? back, string fallback) => back is { Length: > 0 } b && b.StartsWith('/') && !b.StartsWith("//") && !b.Contains('\\') ? b : fallback;

    public static void MapAssetControls(this WebApplication app)
    {
        var g = app.MapGroup("/assets").RequireAuthorization(Roles.Edit);

        g.MapPost("{id:guid}/pause", async (Guid id, AppDbContext db, [FromForm] string? until, [FromForm] string? reason, [FromForm] string? back) =>
        {
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id);
            if (asset is null) return Results.NotFound();
            if (!TryEnd(until, out var resume)) return Results.BadRequest(new { error = "invalid_date" });
            asset.PausedAt = DateTimeOffset.UtcNow;
            asset.PausedUntil = resume;
            asset.PausedReason = Clean(reason);
            await db.Jobs.Where(j => j.AssetId == id).ExecuteDeleteAsync(); // what was queued is not needed any more
            await db.SaveChangesAsync();
            return Results.Redirect(Back(back, $"/assets/{id}"));
        });

        g.MapPost("{id:guid}/resume", async (Guid id, AppDbContext db, JobQueue queue, [FromForm] string? back) =>
        {
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id);
            if (asset is null) return Results.NotFound();
            asset.PausedAt = null; asset.PausedUntil = null; asset.PausedReason = null;
            await db.SaveChangesAsync();
            await queue.EnqueueAsync(asset); // look again right away
            return Results.Redirect(Back(back, $"/assets/{id}"));
        });

        g.MapPost("{id:guid}/mute", async (Guid id, AppDbContext db, [FromForm] string? check, [FromForm] string? code, [FromForm] string? until, [FromForm] string? reason, [FromForm] string? back) =>
        {
            if (!TryEnd(until, out var end)) return Results.BadRequest(new { error = "invalid_date" });
            if (check is null || !CheckTypes.Contains(check) || code is null || !ValidCode().IsMatch(code)) return Results.BadRequest(new { error = "invalid" });
            if (!await db.Assets.AnyAsync(a => a.Id == id)) return Results.NotFound();
            var existing = await db.Mutes.FirstOrDefaultAsync(m => m.AssetId == id && m.CheckType == check && m.Code == code);
            if (existing is null) db.Mutes.Add(new CheckMute { AssetId = id, CheckType = check, Code = code, Reason = Clean(reason), Until = end });
            else { existing.Reason = Clean(reason) ?? existing.Reason; existing.Until = end; }
            await db.SaveChangesAsync();
            return Results.Redirect(Back(back, $"/assets/{id}"));
        });

        g.MapPost("{id:guid}/unmute", async (Guid id, AppDbContext db, [FromForm] string? check, [FromForm] string? code, [FromForm] string? back) =>
        {
            if (!await db.Assets.AnyAsync(a => a.Id == id)) return Results.NotFound();
            await db.Mutes.Where(m => m.AssetId == id && m.CheckType == check && m.Code == code).ExecuteDeleteAsync();
            return Results.Redirect(Back(back, $"/assets/{id}"));
        });

        var c = app.MapGroup("/certificates").RequireAuthorization(Roles.Edit);
        c.MapPost("{id:guid}/mute", async (Guid id, AppDbContext db, [FromForm] string? until, [FromForm] string? reason, [FromForm] string? back) =>
            TryEnd(until, out var end) ? await MuteCertificate(id, db, true, end, Clean(reason), back) : Results.BadRequest(new { error = "invalid_date" }));
        c.MapPost("{id:guid}/unmute", async (Guid id, AppDbContext db, [FromForm] string? back) =>
            await MuteCertificate(id, db, false, null, null, back));
    }

    static async Task<IResult> MuteCertificate(Guid id, AppDbContext db, bool muted, DateTimeOffset? until, string? reason, string? back)
    {
        var cert = await db.Certificates.FirstOrDefaultAsync(x => x.Id == id);
        if (cert is null) return Results.NotFound();
        cert.Muted = muted;
        cert.MutedReason = muted ? reason : null;
        cert.MutedUntil = muted ? until : null;
        await db.SaveChangesAsync();
        return Results.Redirect(Back(back, $"/assets/{cert.AssetId}"));
    }
}
