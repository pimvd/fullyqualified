using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

public class CheckRunner(AppDbContext db, JobQueue queue, IEnumerable<ICheck> checks, AlertService alerts, ILogger<CheckRunner> log)
{
    const int MaxDiscoveredAssets = 200;

    /// <returns>false when the queue is empty.</returns>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var job = await queue.ClaimAsync(ct);
        if (job is null) return false;

        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == job.AssetId, ct);
        var check = checks.FirstOrDefault(c => c.Type == job.CheckType);
        if (asset is not null && check is not null)
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

        db.Results.Add(new CheckResult
        {
            AssetId = asset.Id, CheckType = check.Type, Status = outcome.Status, Summary = outcome.Summary,
            ValuesJson = JsonSerializer.Serialize(outcome.Values),
        });
        foreach (var (key, before, after) in diff)
            db.Changes.Add(new Change { AssetId = asset.Id, CheckType = check.Type, Key = key, Before = before, After = after });

        if (outcome.Certs is not null) await StoreCertsAsync(asset, outcome.Certs, ct);
        if (outcome.DiscoveredHosts is not null) await AddDiscoveredAsync(outcome.DiscoveredHosts, ct);
        await db.SaveChangesAsync(ct);

        await AlertAsync(asset, check.Type, previous?.Status, outcome, diff, ct);
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
                AssetId = asset.Id, Source = c.Source, Host = c.Host, Subject = c.Subject, Issuer = c.Issuer,
                Fingerprint = c.Fingerprint, NotBefore = c.NotBefore, NotAfter = c.NotAfter, Renewal = c.Renewal,
            });
        }
    }

    async Task AddDiscoveredAsync(List<string> hosts, CancellationToken ct)
    {
        var known = (await db.Assets.Select(a => a.Name).ToListAsync(ct)).ToHashSet();
        var room = MaxDiscoveredAssets - await db.Assets.CountAsync(a => a.Discovered, ct);
        foreach (var host in hosts.Where(h => !known.Contains(h)).Take(Math.Max(room, 0)))
        {
            var asset = new Asset { Kind = AssetKind.Endpoint, Name = host, Discovered = true };
            db.Assets.Add(asset);
            foreach (var c in checks.Where(c => c.AppliesTo(asset)))
                db.Jobs.Add(new Job { AssetId = asset.Id, CheckType = c.Type });
        }
    }

    async Task AlertAsync(Asset asset, string type, CheckStatus? before, CheckOutcome outcome,
        List<(string Key, string Before, string After)> diff, CancellationToken ct)
    {
        if (!alerts.HasChannels) return;
        var worsened = outcome.Status >= CheckStatus.Warning && outcome.Status > (before ?? CheckStatus.Ok);
        var recovered = outcome.Status == CheckStatus.Ok && before >= CheckStatus.Warning;
        if (!worsened && !recovered && diff.Count == 0) return;

        var lines = new List<string> { $"{asset.Name} [{type}] {before?.ToString() ?? "new"} -> {outcome.Status}", outcome.Summary.Replace('|', ' ') };
        lines.AddRange(diff.Select(d => $"{d.Key}: '{d.Before}' -> '{d.After}'"));
        await alerts.SendAsync($"FullyQualified: {asset.Name} {outcome.Status}", string.Join("\n", lines), ct);
    }
}
