using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

/// <summary>Uncached network observations. Never logs or retains targets or results.</summary>
public class ToolNetwork(IConfiguration configuration)
{
    public IConfiguration Configuration { get; } = configuration;
    public int ConnectionSeconds => Math.Clamp(Configuration.GetValue("Toolbox:ConnectionSeconds", 5), 1, 30);
    public int MaxAddresses => Math.Clamp(Configuration.GetValue("Toolbox:MaxAddresses", 8), 1, 32);
    public int MaxMxHosts => Math.Clamp(Configuration.GetValue("Toolbox:MaxMxHosts", 10), 1, 30);
    public int MaxPolicyBytes => Math.Clamp(Configuration.GetValue("Toolbox:MaxPolicyBytes", 256 * 1024), 1024, 1024 * 1024);

    public static string? NormalizeHost(string? input)
    {
        var value = (input ?? "").Trim().TrimEnd('.');
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (IPAddress.TryParse(value, out var ip)) return ip.ToString();
        try { value = new IdnMapping().GetAscii(value).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
        if (value.Length is 0 or > 253 || value.Split('.').Any(l => l.Length is 0 or > 63
            || !char.IsAsciiLetterOrDigit(l[0]) || !char.IsAsciiLetterOrDigit(l[^1])
            || l.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return null;
        return value;
    }

    public virtual async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var name = NormalizeHost(host) ?? throw new ToolException("invalid_host");
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(name, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(name, ct); }
        catch (SocketException) { throw new ToolException("dns_failed"); }
        if (addresses.Length == 0) throw new ToolException("dns_failed");
        addresses = addresses.Distinct().ToArray();
        await GuardAddressesAsync(addresses, ct);
        return addresses;
    }

    public virtual async Task GuardAddressesAsync(IEnumerable<IPAddress> addresses, CancellationToken ct)
    {
        if (Configuration.GetValue("Scanning:AllowPrivateAddresses", true)) return;
        var guard = HostGuard.Strict();
        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Any) || ip.IsIPv6Multicast
                || !(await guard.ResolveAsync(ip.ToString(), ct)).Allowed)
                throw new ToolException("blocked");
        }
    }

    public virtual async Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
    {
        var server = Configuration["Dns:Server"];
        var options = IPAddress.TryParse(server, out var ip) ? new LookupClientOptions(ip) : new LookupClientOptions();
        options.UseCache = false;
        options.Timeout = TimeSpan.FromSeconds(ConnectionSeconds);
        options.Retries = 0;
        options.ThrowDnsErrors = false;
        try { return await new LookupClient(options).QueryAsync(name, type, cancellationToken: ct); }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or TimeoutException) { throw new ToolException("dns_failed"); }
    }

    public virtual async Task<IDnsQueryResponse> QueryAtAsync(string name, QueryType type, IPAddress server, CancellationToken ct)
    {
        await GuardAddressesAsync([server], ct);
        try
        {
            return await new LookupClient(new LookupClientOptions(server) { UseCache = false,
                Timeout = TimeSpan.FromSeconds(ConnectionSeconds), Retries = 0, ThrowDnsErrors = false })
                .QueryAsync(name, type, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or TimeoutException) { throw new ToolException("dns_failed"); }
    }

    public virtual async Task<string> FetchPolicyAsync(string host, CancellationToken ct)
    {
        host = NormalizeHost(host) ?? throw new ToolException("invalid_host");
        var addresses = await ResolveAsync(host, ct);
        using var handler = CreatePolicyHandler(addresses, ConnectionSeconds);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(ConnectionSeconds) };
        using var response = await client.GetAsync($"https://{host}/.well-known/mta-sts.txt", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ToolException("http_failed");
        if (response.Content.Headers.ContentLength > MaxPolicyBytes) throw new ToolException("too_large");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > MaxPolicyBytes) throw new ToolException("too_large");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    static SocketsHttpHandler CreatePolicyHandler(IPAddress[] addresses, int seconds) => new()
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(seconds),
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            CertificateChainPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            }
        },
        ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    };

}
