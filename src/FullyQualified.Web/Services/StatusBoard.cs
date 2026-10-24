using Microsoft.EntityFrameworkCore;

namespace FullyQualified.Web;

/// <param name="Status">The status once muted findings are left out; null when the whole check is optional.</param>
/// <param name="Active">Findings that count.</param>
/// <param name="Silenced">Findings that are muted: still shown, but not a warning and never an alarm.</param>
public record EffectiveResult(CheckStatus? Status, bool Optional, List<string> Active, List<string> Silenced);

public static class Effective
{
    /// <summary>
    /// The findings behind a result, as codes that can be muted. DNS findings are the stored issues ("NoCaa", "DkimMissing:s1"),
    /// the other checks have one code each for what can go wrong: "TlsExpired", "TlsExpiring", "TlsTrust", "TlsError", "CtError".
    /// </summary>
    public static List<string> Codes(CheckResult r)
    {
        if (r.Status == CheckStatus.Ok) return [];
        switch (r.CheckType)
        {
            case "dns":
                var issues = (r.Values.GetValueOrDefault("_issues") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (issues.Count == 0) issues.Add("DnsError");
                return issues;
            case "tls":
                return [r.Summary.Split('|')[0] switch
                {
                    "Sum_TlsExpired" => "TlsExpired",
                    "Sum_TlsOk" => "TlsExpiring",
                    "Sum_TlsChain" or "Sum_TlsChainMismatch" => "TlsTrust",
                    _ => "TlsError",
                }];
            case "ct": return ["CtError"];
            default: return [r.CheckType + "Error"];
        }
    }

    /// <param name="muted">The muted codes of this asset and check; "*" mutes the whole check.</param>
    /// <param name="certMuted">The certificate the TLS check found is muted, so its expiry does not count.</param>
    public static EffectiveResult Of(CheckResult r, IReadOnlyCollection<string> muted, bool certMuted = false)
    {
        // Certificate-log discovery is optional by nature: it runs on request, and what it finds is reviewed, not judged.
        if (r.CheckType == "ct") return new(null, true, [], []);
        var codes = Codes(r);
        if (muted.Contains(CheckMute.WholeCheck)) return new(null, true, [], codes);

        var active = codes.Where(c => !muted.Contains(c) && !(certMuted && c is "TlsExpiring" or "TlsExpired")).ToList();
        var silenced = codes.Except(active).ToList();
        var status = active.Count == 0 ? CheckStatus.Ok
            : r.CheckType == "dns" ? (active.Contains("DanglingCname") ? CheckStatus.Critical : active.Contains("DnsError") ? CheckStatus.Error : CheckStatus.Warning)
            : r.Status;
        return new(status, false, active, silenced);
    }
}

public record AssetStatus(Asset Asset, bool Paused, List<(CheckResult Result, EffectiveResult Effective)> Checks)
{
    /// <summary>The worst status that counts. Null for a paused asset, an asset without results, and an asset whose checks are all optional.</summary>
    public CheckStatus? Worst => Paused ? null : Checks.Select(c => c.Effective.Status).Where(s => s is not null).DefaultIfEmpty(null).Max();

    /// <summary>Findings and checks that are muted, for a short "n muted" note.</summary>
    public int MutedCount => Checks.Where(c => c.Result.CheckType != "ct").Sum(c => c.Effective.Optional ? Math.Max(1, c.Effective.Silenced.Count) : c.Effective.Silenced.Count);

    public bool NeedsAttention => Worst >= CheckStatus.Warning;
}

public static class StatusBoard
{
    /// <summary>What every asset looks like once pauses and mutes are applied. This is what the dashboard, the counts and the alarms go by.</summary>
    public static Dictionary<Guid, AssetStatus> Build(IEnumerable<Asset> assets, IReadOnlyCollection<CheckResult> latest,
        IReadOnlyCollection<CheckMute> mutes, IReadOnlyCollection<CertificateRecord> certs, DateTimeOffset now)
    {
        // A mute with an end date stops counting when it passes; nothing has to run for that.
        var mutedBy = mutes.Where(m => m.IsActive(now)).GroupBy(m => (m.AssetId, m.CheckType)).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(m => m.Code).ToList());
        var mutedCerts = certs.Where(c => c.IsMuted(now) && c.Source == "endpoint").Select(c => c.AssetId).ToHashSet();
        return assets.ToDictionary(a => a.Id, a => new AssetStatus(a, a.IsPaused(now),
            latest.Where(r => r.AssetId == a.Id).OrderBy(r => r.CheckType)
                .Select(r => (r, Effective.Of(r, mutedBy.GetValueOrDefault((a.Id, r.CheckType)) ?? [], mutedCerts.Contains(a.Id)))).ToList()));
    }

    public static async Task<Dictionary<Guid, AssetStatus>> LoadAsync(AppDbContext db, DateTimeOffset now)
    {
        var assets = await db.Assets.ToListAsync();
        return Build(assets, await db.LatestResultsAsync(), await db.Mutes.ToListAsync(),
            await db.Certificates.Where(c => c.Muted && c.Source == "endpoint").ToListAsync(), now);
    }
}
