using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

public record DigRecord(string Name, int Ttl, string Class, string Type, string Data);
public record DigResolver(string Key, string Label);
public record DigResult(string Resolver, string Label, string Server, string Status, bool Authoritative, bool Truncated, bool RecursionAvailable,
    int Ms, List<DigRecord> Answers, List<DigRecord> Authority, string? Error);

/// <summary>A live DNS lookup, like dig, against the system resolver and the configured comparison resolvers.</summary>
public partial class DigService
{
    public static readonly string[] Types = ["A", "AAAA", "CNAME", "MX", "NS", "TXT", "SOA", "CAA", "DS", "DNSKEY", "SRV", "PTR"];

    static readonly Dictionary<string, string> Providers = new()
    {
        ["1.1.1.1"] = "Cloudflare", ["1.0.0.1"] = "Cloudflare", ["8.8.8.8"] = "Google", ["8.8.4.4"] = "Google", ["9.9.9.9"] = "Quad9", ["149.112.112.112"] = "Quad9",
    };

    readonly List<(DigResolver Info, LookupClient Client)> resolvers = [];
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> recent = new();

    public DigService(IConfiguration cfg)
    {
        resolvers.Add((new("system", cfg["Dns:Server"] is { Length: > 0 } s ? $"System resolver ({s})" : "System resolver"),
            new LookupClient(Options(cfg["Dns:Server"] is { Length: > 0 } custom && IPAddress.TryParse(custom, out var parsed) ? parsed : null))));
        foreach (var ip in (cfg["Dns:CompareResolvers"] ?? "1.1.1.1,9.9.9.9,8.8.8.8").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (IPAddress.TryParse(ip, out var address))
                resolvers.Add((new(ip, Providers.TryGetValue(ip, out var name) ? $"{name} ({ip})" : ip), new LookupClient(Options(address))));
    }

    static LookupClientOptions Options(IPAddress? server) =>
        new(server is null ? NameServer.ResolveNameServers().ToArray() : [new NameServer(server)])
        { UseCache = false, Timeout = TimeSpan.FromSeconds(4), Retries = 1, ThrowDnsErrors = false };

    public IReadOnlyList<DigResolver> Resolvers => resolvers.Select(r => r.Info).ToList();

    [GeneratedRegex(@"^(?=.{1,253}\.?$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)*[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.?$")]
    private static partial Regex Valid();

    /// <summary>Lower-cases and trims; accepts host names (underscore labels such as _dmarc too) and IP addresses. Null when invalid.</summary>
    public static string? NormalizeName(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0) return null;
        return IPAddress.TryParse(s, out _) || Valid().IsMatch(s) ? s : null;
    }

    /// <summary>At most 60 lookups per minute per user, so the page cannot be used to flood a resolver.</summary>
    public bool Allow(string who)
    {
        var q = recent.GetOrAdd(who, _ => new());
        lock (q)
        {
            var now = DateTimeOffset.UtcNow;
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= 60) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <param name="resolverKey">"all", or the key of one resolver.</param>
    public async Task<List<DigResult>?> DigAsync(string name, string type, string resolverKey, CancellationToken ct)
    {
        var chosen = resolverKey == "all" ? resolvers : resolvers.Where(r => r.Info.Key == resolverKey).ToList();
        if (chosen.Count == 0) return null;
        return (await Task.WhenAll(chosen.Select(r => One(r.Info, r.Client, name, type, ct)))).ToList();
    }

    static async Task<DigResult> One(DigResolver info, LookupClient client, string name, string type, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var query = type == "PTR" && IPAddress.TryParse(name, out var ip)
                ? await client.QueryReverseAsync(ip, cancellationToken: ct)
                : await client.QueryAsync(name, Enum.Parse<QueryType>(type), cancellationToken: ct);
            timer.Stop();
            return new(info.Key, info.Label, query.NameServer.Address.ToString(), Rcode(query.Header.ResponseCode),
                query.Header.HasAuthorityAnswer, query.Header.ResultTruncated, query.Header.RecursionAvailable, (int)timer.ElapsedMilliseconds,
                query.Answers.Select(Map).ToList(), query.Authorities.Select(Map).ToList(), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is DnsResponseException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return new(info.Key, info.Label, "", "ERROR", false, false, false, (int)timer.ElapsedMilliseconds, [], [], ex.Message);
        }
    }

    static DigRecord Map(DnsResourceRecord r) => new(r.DomainName.Value, (int)r.TimeToLive, r.RecordClass.ToString().ToUpperInvariant(), r.RecordType.ToString(), Data(r));

    /// <summary>The record data in zone-file form: what follows "name ttl class type" in the record's own text.</summary>
    static string Data(DnsResourceRecord r)
    {
        var parts = r.ToString().Split([' ', '\t'], 5, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 ? parts[4].Trim() : "";
    }

    static string Rcode(DnsHeaderResponseCode code) => code switch
    {
        DnsHeaderResponseCode.NoError => "NOERROR", DnsHeaderResponseCode.FormatError => "FORMERR", DnsHeaderResponseCode.ServerFailure => "SERVFAIL",
        DnsHeaderResponseCode.NotExistentDomain => "NXDOMAIN", DnsHeaderResponseCode.NotImplemented => "NOTIMP", DnsHeaderResponseCode.Refused => "REFUSED",
        _ => code.ToString().ToUpperInvariant(),
    };
}
