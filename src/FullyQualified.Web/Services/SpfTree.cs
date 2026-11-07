using System.Net;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

/// <summary>
/// One entry of the SPF tree: an SPF record (<c>domain</c>, <c>include</c>, <c>redirect</c>), a mechanism (<c>a</c>, <c>mx</c>, <c>ip4</c>, ...),
/// a mail host or an address. <see cref="Note"/> is a code the page turns into text, for what could not be expanded.
/// </summary>
public sealed class SpfNode(string kind, string text, string? qualifier = null)
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    /// <summary>"+", "-", "~" or "?" for a mechanism, null for a record's own root and for addresses found below a mechanism.</summary>
    public string? Qualifier { get; } = qualifier;
    /// <summary>The record as published, on the nodes that are a record.</summary>
    public string? Record { get; set; }
    public string? Note { get; set; }
    public List<SpfNode> Children { get; } = [];
}

public sealed record SpfTree(SpfNode Root, int Lookups, bool Truncated);

/// <summary>
/// Resolves an SPF record into a tree: every <c>include</c> and <c>redirect</c> is followed, and <c>a</c> and <c>mx</c> are
/// resolved down to their addresses. It reads DNS only, through the toolbox's resolver, and stops at fixed limits so a
/// hostile or huge record cannot make it run away. It does not evaluate anything: it shows what the record points at.
/// </summary>
public sealed partial class SpfTreeBuilder
{
    readonly Func<string, QueryType, CancellationToken, Task<IDnsQueryResponse>> query;
    readonly bool expandAddresses;

    public SpfTreeBuilder(ToolNetwork network) : this(network.QueryAsync) { }

    /// <summary>
    /// Resolves through <paramref name="query"/>, so the DNS check can use its own resolver. With <paramref name="expandAddresses"/> off,
    /// <c>a</c> and <c>mx</c> are counted but not resolved to hosts and addresses: enough to count DNS lookups, and far fewer queries.
    /// </summary>
    internal SpfTreeBuilder(Func<string, QueryType, CancellationToken, Task<IDnsQueryResponse>> query, bool expandAddresses = true)
    {
        this.query = query;
        this.expandAddresses = expandAddresses;
    }

    public const int MaxDepth = 10, MaxNodes = 400, MaxQueries = 80, MaxMxHosts = 10, MaxAddresses = 20;

    [GeneratedRegex(@"^(?<q>[+\-~?])?(?<m>all|include|a|mx|ptr|ip4|ip6|exists)(?::(?<arg>[^/\s]+))?(?<c4>/\d{1,3})?(?<c6>//\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex Mechanism();

    sealed class Run
    {
        public int Queries, Nodes, Lookups;
        public bool Truncated;
        public readonly HashSet<string> Path = new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SpfTree> BuildAsync(string domain, CancellationToken ct)
    {
        var run = new Run();
        var root = await RecordAsync(domain, "domain", null, run, 0, ct);
        return new(root, run.Lookups, run.Truncated);
    }

    SpfNode? Add(SpfNode parent, SpfNode child, Run run)
    {
        if (++run.Nodes > MaxNodes) { run.Truncated = true; parent.Note ??= "limit"; return null; }
        parent.Children.Add(child);
        return child;
    }

    async Task<IDnsQueryResponse?> QueryAsync(string name, QueryType type, Run run, CancellationToken ct)
    {
        if (++run.Queries > MaxQueries) { run.Truncated = true; return null; }
        try
        {
            var response = await query(name, type, ct);
            MailTools.CheckResponse(response);
            return response;
        }
        catch (Exception ex) when (ex is ToolException or DnsResponseException) { return null; }
    }

    /// <summary>
    /// A DNS name to look up, or null for a macro or something that is not a name. Unlike <see cref="ToolNetwork.NormalizeHost"/> this allows
    /// underscores, which SPF includes use all the time (<c>_spf.google.com</c>); it is safe because the name is only ever queried, never connected to.
    /// </summary>
    static string? PlainDomain(string? value)
    {
        if (value is null || value.Contains('%')) return null;
        var name = value.Trim().TrimEnd('.');
        if (name.Length == 0 || IPAddress.TryParse(name, out _)) return null;
        try { name = new System.Globalization.IdnMapping().GetAscii(name).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
        return name.Length > 253 || name.Split('.').Any(l => l.Length is 0 or > 63 || l.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            ? null : name;
    }

    async Task<SpfNode> RecordAsync(string domain, string kind, string? qualifier, Run run, int depth, CancellationToken ct)
    {
        var node = new SpfNode(kind, domain, qualifier);
        if (!run.Path.Add(domain)) { node.Note = "loop"; return node; }
        try
        {
            if (depth > MaxDepth) { node.Note = "depth"; run.Truncated = true; return node; }
            var answer = await QueryAsync(domain, QueryType.TXT, run, ct);
            if (answer is null) { node.Note = run.Queries > MaxQueries ? "limit" : "dns_error"; return node; }
            var records = answer.Answers.TxtRecords().Select(t => string.Concat(t.Text)).Where(MailRules.IsSpf).ToList();
            if (records.Count == 0) { node.Note = "no_record"; return node; }
            node.Record = records[0];
            if (records.Count > 1) node.Note = "multiple";

            string? redirect = null;
            var hasAll = false;
            foreach (var word in records[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                if (word.StartsWith("redirect=", StringComparison.OrdinalIgnoreCase)) { redirect = word[9..]; continue; }
                if (word.Contains('=')) continue;                     // exp= and unknown modifiers add no senders
                var m = Mechanism().Match(word);
                if (!m.Success) { if (Add(node, new SpfNode("unknown", word), run) is null) break; continue; }
                var q = m.Groups["q"].Success ? m.Groups["q"].Value : "+";
                var name = m.Groups["m"].Value.ToLowerInvariant();
                var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value : null;
                var c4 = m.Groups["c4"].Value;      // "/24": the IPv4 prefix length
                var c6 = m.Groups["c6"].Value;      // "//64": the IPv6 prefix length
                if (name == "all") { hasAll = true; Add(node, new SpfNode("all", "all", q), run); break; }
                if (await MechanismAsync(node, name, arg, c4, c6, q, domain, run, depth, ct) is false) break;
            }
            if (!hasAll && redirect is not null)
            {
                run.Lookups++;
                if (PlainDomain(redirect) is { } target) { if (Add(node, await RecordAsync(target, "redirect", "+", run, depth + 1, ct), run) is null) return node; }
                else Add(node, new SpfNode("redirect", redirect, "+") { Note = "macro" }, run);
            }
            return node;
        }
        finally { run.Path.Remove(domain); }
    }

    /// <summary>Adds one mechanism and what it resolves to. False when the node limit stopped the walk.</summary>
    async Task<bool> MechanismAsync(SpfNode parent, string name, string? arg, string c4, string c6, string q, string domain, Run run, int depth, CancellationToken ct)
    {
        switch (name)
        {
            case "ip4" or "ip6":
                return Add(parent, new SpfNode(name, (arg ?? "") + c4 + c6, q), run) is not null;
            case "ptr" or "exists":
                run.Lookups++;
                return Add(parent, new SpfNode(name, arg ?? domain, q) { Note = name }, run) is not null;
            case "include":
                run.Lookups++;
                if (PlainDomain(arg) is not { } include)
                    return Add(parent, new SpfNode("include", arg ?? "", q) { Note = "macro" }, run) is not null;
                return Add(parent, await RecordAsync(include, "include", q, run, depth + 1, ct), run) is not null;
            default: // a, mx
                run.Lookups++;
                var host = arg is null ? domain : PlainDomain(arg);
                var node = new SpfNode(name, (host ?? arg ?? "") + c4 + c6, q);
                if (Add(parent, node, run) is null) return false;
                if (host is null) { node.Note = "macro"; return true; }
                if (!expandAddresses) return true;
                if (name == "a") return await AddAddressesAsync(node, host, c4, c6, run, ct);
                var mx = await QueryAsync(host, QueryType.MX, run, ct);
                if (mx is null) { node.Note = "dns_error"; return true; }
                foreach (var record in mx.Answers.MxRecords().OrderBy(r => r.Preference).Take(MaxMxHosts))
                {
                    var exchange = record.Exchange.Value.TrimEnd('.');
                    if (exchange.Length == 0) continue;                // the null MX
                    var hostNode = Add(node, new SpfNode("host", exchange), run);
                    if (hostNode is null || !await AddAddressesAsync(hostNode, exchange, c4, c6, run, ct)) return false;
                }
                return true;
        }
    }

    async Task<bool> AddAddressesAsync(SpfNode node, string host, string c4, string c6, Run run, CancellationToken ct)
    {
        var found = new List<IPAddress>();
        foreach (var type in new[] { QueryType.A, QueryType.AAAA })
            if (await QueryAsync(host, type, run, ct) is { } answer)
                found.AddRange(type == QueryType.A ? answer.Answers.ARecords().Select(r => r.Address) : answer.Answers.AaaaRecords().Select(r => r.Address));
        if (found.Count == 0) { node.Note ??= "no_addresses"; return true; }
        foreach (var ip in found.Take(MaxAddresses))
        {
            var v4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            var mask = v4 ? c4 : (c6.Length > 0 ? c6[1..] : "");     // each prefix length applies to its own address family
            if (Add(node, new SpfNode(v4 ? "ip4" : "ip6", ip + mask), run) is null) return false;
        }
        if (found.Count > MaxAddresses) { node.Note = "limit"; run.Truncated = true; }
        return true;
    }
}
