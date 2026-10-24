using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public class CheckRunner(AppDbContext db, JobQueue queue, IEnumerable<ICheck> checks, AlertService alerts, PlanService plans, IConfiguration cfg, Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l, ILogger<CheckRunner> log)
{
    /// <returns>false when the queue is empty.</returns>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var job = await queue.ClaimAsync(ct);
        if (job is null) return false;

        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == job.AssetId, ct);
        var check = checks.FirstOrDefault(c => c.Type == job.CheckType);
        // A job queued before the asset was paused is dropped, not run.
        if (asset is not null && check is not null && !asset.IsPaused(DateTimeOffset.UtcNow))
        {
            try { await ExecuteAsync(asset, check, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Check {Check} on {Asset} failed", check.Type, asset.Name); }
        }
        db.Jobs.Remove(job);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ExecuteAsync(Asset asset, ICheck check, CancellationToken ct)
    {
        CheckOutcome outcome;
        try { outcome = await check.RunAsync(asset, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            outcome = new(CheckStatus.Error, $"Sum_Error|{ex.Message.Replace('|', '/')}", []);
        }

        var previous = await db.Results.Where(r => r.AssetId == asset.Id && r.CheckType == check.Type)
            .OrderByDescending(r => r.ObservedAt).FirstOrDefaultAsync(ct);
        // An errored run has no baseline of its own; changes are measured against the last good run.
        var lastGood = await db.Results.Where(r => r.AssetId == asset.Id && r.CheckType == check.Type && r.Status != CheckStatus.Error)
            .OrderByDescending(r => r.ObservedAt).FirstOrDefaultAsync(ct);
        var diff = outcome.Status == CheckStatus.Error ? [] : ChangeDetector.Diff(lastGood?.Values, outcome.Values);

        var stored = new CheckResult
        {
            TenantId = asset.TenantId, AssetId = asset.Id, CheckType = check.Type, Status = outcome.Status, Summary = outcome.Summary,
            ValuesJson = JsonSerializer.Serialize(outcome.Values),
        };
        db.Results.Add(stored);
        foreach (var (key, before, after) in diff)
            db.Changes.Add(new Change { TenantId = asset.TenantId, AssetId = asset.Id, CheckType = check.Type, Key = key, Before = before, After = after });

        if (outcome.Certs is not null) await StoreCertsAsync(asset, outcome.Certs, ct);
        if (outcome.DiscoveredHosts is not null) await RecordDiscoveredAsync(asset, outcome.DiscoveredHosts, ct);
        await db.SaveChangesAsync(ct);

        // The certificate-log search is run by hand and reviewed by hand: it never raises an alarm.
        if (check.Type == "ct") return;

        // Alarms go by what counts: muted findings and optional checks do not raise or clear anything.
        var now = DateTimeOffset.UtcNow;
        var muted = await db.Mutes.Where(m => m.AssetId == asset.Id && m.CheckType == check.Type && (m.Until == null || m.Until > now)).Select(m => m.Code).ToListAsync(ct);
        if (muted.Contains(CheckMute.WholeCheck)) return;
        var certMuted = await db.Certificates.AnyAsync(c => c.AssetId == asset.Id && c.Source == "endpoint" && c.Muted && (c.MutedUntil == null || c.MutedUntil > now), ct);
        CheckStatus? statusBefore = previous is null ? null : Effective.Of(previous, muted, certMuted).Status ?? CheckStatus.Ok;
        var effective = Effective.Of(stored, muted, certMuted);
        var statusAfter = effective.Status ?? CheckStatus.Ok;
        await AlertAsync(asset, check.Type, statusBefore, statusAfter, outcome.Summary, effective.Active, diff, ct);
    }

    /// <summary>Certificates are stored as a snapshot per asset and source: the current state, not the history.</summary>
    async Task StoreCertsAsync(Asset asset, List<CertInfo> certs, CancellationToken ct)
    {
        var source = certs.FirstOrDefault()?.Source;
        if (source is null) return;
        var old = await db.Certificates.Where(c => c.AssetId == asset.Id && c.Source == source).ToListAsync(ct);
        db.Certificates.RemoveRange(old.Where(o => certs.All(c => c.Fingerprint != o.Fingerprint)));
        foreach (var c in certs)
        {
            var existing = old.FirstOrDefault(o => o.Fingerprint == c.Fingerprint);
            if (existing is not null) { existing.SeenAt = DateTimeOffset.UtcNow; continue; }
            db.Certificates.Add(new CertificateRecord
            {
                TenantId = asset.TenantId, AssetId = asset.Id, Source = c.Source, Host = c.Host, Subject = c.Subject, Issuer = c.Issuer,
                Fingerprint = c.Fingerprint, NotBefore = c.NotBefore, NotAfter = c.NotAfter, Renewal = c.Renewal,
            });
        }
    }

    /// <summary>
    /// Files what a search found for review. Nothing is monitored yet: the person decides per hostname. A hostname that is already an asset,
    /// or already decided, keeps its state; only what we know about it is refreshed.
    /// </summary>
    async Task RecordDiscoveredAsync(Asset origin, List<DiscoveredHost> hosts, CancellationToken ct)
    {
        // Background work sees every tenant, so duplicates are looked for within the tenant that owns the asset.
        var known = (await db.Assets.Where(a => a.TenantId == origin.TenantId).Select(a => a.Name).ToListAsync(ct)).ToHashSet();
        var filed = await db.ReviewHosts.Where(h => h.TenantId == origin.TenantId).ToDictionaryAsync(h => h.Name, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var host in hosts.Where(h => !known.Contains(h.Name)))
        {
            if (filed.TryGetValue(host.Name, out var existing))
            {
                existing.LastSeenAt = now; existing.Service = host.Service; existing.Issuer = host.Issuer; existing.CertExpires = host.CertExpires;
                continue;
            }
            db.ReviewHosts.Add(new ReviewHost
            {
                TenantId = origin.TenantId, ParentAssetId = origin.Id, Name = host.Name, Service = host.Service, Issuer = host.Issuer, CertExpires = host.CertExpires,
            });
        }
    }

    async Task AlertAsync(Asset asset, string type, CheckStatus? before, CheckStatus after, string summary, List<string> activeCodes,
        List<(string Key, string Before, string After)> diff, CancellationToken ct)
    {
        var config = await alerts.ConfigFor(asset.TenantId, ct);
        if (!AlertPolicy.ShouldAlert(config, before, after, diff.Count)) return;

        // Say it in words: what was checked, what changed, and what was found.
        var lines = new List<string> { $"{asset.Name}: {l["Check_" + type].Value}: {before?.ToString() ?? "new"} → {after}", ResourceText.Summary(l, summary) };
        if (type == "dns") lines.AddRange(activeCodes.Select(code => "• " + ResourceText.IssueText(l, code)));
        lines.AddRange(diff.Select(d => $"{d.Key}: '{d.Before}' -> '{d.After}'"));
        // Help the person who gets the alarm: a link to what to do about each finding, when we know our own address.
        if (cfg["Auth:Origin"] is { Length: > 0 } origin && after >= CheckStatus.Warning)
            lines.AddRange(activeCodes.Take(3).Select(code => $"How to fix: {Remedies.Link(origin, asset.Id, type, code)}"));
        await alerts.SendAsync(asset.TenantId, $"FullyQualified: {asset.Name} {after}", string.Join("\n", lines), ct);
    }
}
