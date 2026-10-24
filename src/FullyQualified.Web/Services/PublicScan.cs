using System.Collections.Concurrent;
using System.Net;

namespace FullyQualified.Web;

public record PublicScanResult(string Domain, CheckOutcome Tls, CheckOutcome Dns);

/// <summary>Limits how often one address may scan and how many scans run at once. Everything lives in memory and nothing is stored.</summary>
public class ScanThrottle(IConfiguration cfg)
{
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> seen = new();
    readonly SemaphoreSlim running = new(cfg.GetValue("PublicScan:Concurrent", 8), cfg.GetValue("PublicScan:Concurrent", 8));
    int PerHour => cfg.GetValue("PublicScan:PerHour", 10);

    public bool TryAcquire(string key, DateTimeOffset now)
    {
        if (seen.Count > 10_000) foreach (var (k, q) in seen) { lock (q) { if (q.Count == 0 || q.Peek() < now.AddHours(-1)) seen.TryRemove(k, out _); } }
        var queue = seen.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < now.AddHours(-1)) queue.Dequeue();
            if (queue.Count >= PerHour) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>Null when too many scans are already running.</summary>
    public IDisposable? TryEnter() => running.Wait(0) ? new Releaser(running) : null;

    sealed class Releaser(SemaphoreSlim s) : IDisposable { public void Dispose() => s.Release(); }
}

/// <summary>
/// The free scanner: anyone can check the certificate and DNS of a public domain, without an account. Nothing is stored. Because strangers
/// choose the target, it only accepts public names on port 443, refuses private addresses whatever the settings say, pins the connection to the
/// address it checked, and is rate limited. It is off unless <c>PublicScan:Enabled</c> is set.
/// </summary>
public class PublicScanService(IConfiguration cfg, ScanThrottle throttle, ILogger<PublicScanService> log)
{
    static readonly string[] LocalSuffixes = [".local", ".localhost", ".internal", ".lan", ".home", ".corp", ".intranet", ".arpa", ".test", ".invalid", ".example"];

    public bool Enabled => cfg.GetValue("PublicScan:Enabled", false);

    /// <summary>A public host name without port or path, or null.</summary>
    public static string? Normalize(string? input)
    {
        var name = HostName.Normalize(input);
        if (name is null || name.Contains(':') || IPAddress.TryParse(name, out _)) return null;
        if (name.Split('.').All(label => label.All(char.IsDigit))) return null; // looks like an address, not a name
        return LocalSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)) ? null : name;
    }

    /// <returns>A result, or the reason there is none: invalid, rate_limited, busy, disabled.</returns>
    public async Task<(PublicScanResult? Result, string? Error)> ScanAsync(string? input, IPAddress? client, CancellationToken ct)
    {
        if (!Enabled) return (null, "disabled");
        var domain = Normalize(input);
        if (domain is null) return (null, "invalid");
        if (!throttle.TryAcquire(client?.ToString() ?? "unknown", DateTimeOffset.UtcNow)) return (null, "rate_limited");
        using var slot = throttle.TryEnter();
        if (slot is null) return (null, "busy");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var asset = new Asset { Name = domain, Kind = AssetKind.Domain };
        var tls = Guarded(new TlsCheck(HostGuard.Strict()), asset, cts.Token);
        var dns = Guarded(new DnsCheck(cfg), asset, cts.Token);
        await Task.WhenAll(tls, dns);
        log.LogDebug("Public scan finished"); // the domain is deliberately not logged: nothing about a public check is kept
        return (new PublicScanResult(domain, await tls, await dns), null);
    }

    static async Task<CheckOutcome> Guarded(ICheck check, Asset asset, CancellationToken ct)
    {
        try { return await check.RunAsync(asset, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(CheckStatus.Error, "Sum_Error|the check failed", []); }
        catch (OperationCanceledException) { return new(CheckStatus.Error, "Sum_Error|the check timed out", []); }
    }
}
