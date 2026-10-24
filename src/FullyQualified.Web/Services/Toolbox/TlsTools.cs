using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;

namespace FullyQualified.Web;

/// <summary>Bounded, transient TLS diagnostics. Every socket uses a previously checked IP address.</summary>
public class TlsTools(ToolNetwork network, IConfiguration configuration)
{
    // TLS 1.2 only: TLS 1.3 suite selection is not portable through SslStream.
    static readonly TlsCipherSuite[] CipherCandidates =
    [
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
        TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA
    ];
#pragma warning disable SYSLIB0039 // Intentionally observe obsolete protocols without changing runtime policy.
    static readonly SslProtocols[] ProtocolCandidates = [SslProtocols.Tls, SslProtocols.Tls11, SslProtocols.Tls12, SslProtocols.Tls13];
#pragma warning restore SYSLIB0039

    public async Task<ToolResult> RunAsync(ToolRequest request, CancellationToken ct)
    {
        if (request.Tool == "certificate") return CertificateTools.Inspect(request);
        if (request.Tool is not ("tls" or "starttls" or "mx")) throw new ToolException("invalid_tool");
        var sections = new List<ToolSection>();
        var complete = true;
        try
        {
            if (request.Tool == "mx")
            {
                var domain = ToolNetwork.NormalizeHost(request.Domain) ?? throw new ToolException("invalid_host");
                if (IPAddress.TryParse(domain, out _)) throw new ToolException("invalid_host");
                var response = await network.QueryAsync(domain, QueryType.MX, ct);
                if (response.HasError || response.Header.ResultTruncated) throw new ToolException("dns_failed");
                var records = response.Answers.MxRecords().OrderBy(r => r.Preference).ToArray();
                if (records.Any(r => r.Exchange.Value == "."))
                    return new([new("Tool_MxResults", [new("Tool_MxStatus", "Tool_NullMx", "warning")])]);
                if (records.Length == 0)
                    return new([new("Tool_MxResults", [new("Tool_MxStatus", "Tool_NoMx", "warning")])]);
                var hosts = records.Select(r => r.Exchange.Value.TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var maximum = Math.Min(network.MaxMxHosts, 10);
                if (hosts.Length > maximum)
                {
                    complete = false;
                    sections.Add(new("Tool_MxResults", [new("Tool_MxOmitted", (hosts.Length - maximum).ToString(), "warning")]));
                }
                foreach (var host in hosts.Take(maximum))
                {
                    ct.ThrowIfCancellationRequested();
                    try { complete &= await InspectHost(host, request with { Mode = "smtp", Tool = "starttls", Host = host }, sections, ct); }
                    catch (ToolException ex)
                    {
                        complete = false;
                        sections.Add(new("Tool_MxResults", [new("Tool_TlsHost", host), new("Tool_TlsOutcome", ex.Code == "blocked" ? "Tool_TargetBlocked" : "Tool_TlsUnreachable", "warning")]));
                    }
                }
            }
            else
            {
                var host = ToolNetwork.NormalizeHost(request.Host) ?? throw new ToolException("invalid_host");
                complete &= await InspectHost(host, request, sections, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && sections.Count > 0)
        {
            complete = false;
            sections.Add(new("Tool_TlsLimits", [new("Tool_TlsOutcome", "Tool_TlsRunStopped", "warning")]));
        }
        return new(sections, complete);
    }

    async Task<bool> InspectHost(string host, ToolRequest request, List<ToolSection> sections, CancellationToken ct)
    {
        var mode = request.Tool == "tls" ? "tls" : request.Mode?.ToLowerInvariant() ?? "smtp";
        var defaultPort = mode switch { "tls" => 443, "smtp" => 25, "imap" => 143, "pop3" => 110, "smtps" => 465, "imaps" => 993, "pop3s" => 995, _ => throw new ToolException("invalid_mode") };
        var port = request.Port ?? defaultPort;
        if (port is < 1 or > 65535) throw new ToolException("invalid_port");
        var identity = ToolNetwork.NormalizeHost(request.ServerName ?? host) ?? throw new ToolException("invalid_host");
        var addresses = (await network.ResolveAsync(host, ct)).Distinct().ToArray();
        if (addresses.Length == 0) throw new ToolException("dns_failed");
        // Enforce at this public boundary as well: an injected resolver or overridden guard cannot skip policy.
        await new ToolNetwork(configuration).GuardAddressesAsync(addresses, ct);
        await network.GuardAddressesAsync(addresses, ct);
        var max = Math.Min(network.MaxAddresses, 8);
        var complete = addresses.Length <= max;
        if (!complete) sections.Add(new("Tool_TlsLimits", [new("Tool_TlsAddressesOmitted", (addresses.Length - max).ToString(), "warning")]));
        foreach (var ip in addresses.Take(max))
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<ToolRow> { new("Tool_TlsHost", host), new("Tool_TlsAddress", ip.ToString()), new("Tool_TlsPort", port.ToString()), new("Tool_TlsIdentity", identity) };
            var section = new ToolSection("Tool_TlsEndpoint", rows); sections.Add(section);
            var baseline = await Handshake(ip, port, identity, mode, SslProtocols.None, null, true, ct);
            rows.Add(new("Tool_TlsOutcome", baseline.Outcome, baseline.Success ? "info" : "warning", baseline.Explanation));
            if (mode is "smtp" or "imap" or "pop3")
                rows.Add(new("Tool_StartTlsUpgrade", baseline.Upgraded ? "Tool_Accepted" : baseline.Outcome == "Tool_Rejected" ? "Tool_Rejected" : "Tool_Unknown", baseline.Upgraded ? "info" : "warning"));
            if (!baseline.Success) { complete = false; continue; }
            rows.Add(new("Tool_TlsProtocol", baseline.Protocol.ToString()));
            rows.Add(new("Tool_TlsCipher", baseline.Cipher.ToString()));
            rows.Add(new("Tool_TlsValidation", baseline.Validation.ToString(), baseline.Validation == SslPolicyErrors.None ? "info" : "warning"));
            sections.AddRange(baseline.Sections);
            var protocolRows = new List<ToolRow>();
            sections.Add(new("Tool_TlsProtocolProbes", protocolRows));
            foreach (var protocol in ProtocolCandidates)
            {
                var probe = await Handshake(ip, port, identity, mode, protocol, null, false, ct);
                protocolRows.Add(new("Tool_TlsProtocolProbe", protocol.ToString()));
                protocolRows.Add(new("Tool_TlsProbeOutcome", probe.Outcome, probe.Success ? "info" : "warning", probe.Explanation));
                if (probe.Outcome is "Tool_TlsUnreachable" or "Tool_TlsTimeout" or "Tool_TlsInconclusive") complete = false;
            }
            var cipherRows = new List<ToolRow>
            {
                new("Tool_TlsCipherCandidates", string.Join(", ", CipherCandidates)),
                new("Tool_TlsCipherScope", "Tool_TlsCipherScopeExplanation", "info"),
                new("Tool_Tls13CipherControl", "Tool_TlsLocallyUntestable", "warning", "Tool_Tls13CipherLimitation")
            };
            sections.Add(new("Tool_TlsCipherProbes", cipherRows));
            foreach (var cipher in CipherCandidates)
            {
                var probe = await Handshake(ip, port, identity, mode, SslProtocols.Tls12, cipher, false, ct);
                cipherRows.Add(new("Tool_TlsCipherCandidate", cipher.ToString()));
                cipherRows.Add(new("Tool_TlsProbeOutcome", probe.Outcome, probe.Success && !IsWeak(cipher) ? "info" : "warning", probe.Explanation));
                if (probe.Success && IsWeak(cipher)) cipherRows.Add(new("Tool_TlsWeakCipher", cipher.ToString(), "warning", "Tool_TlsWeakCipherExplanation"));
                if (probe.Outcome is "Tool_TlsUnreachable" or "Tool_TlsTimeout" or "Tool_TlsInconclusive") complete = false;
            }
        }
        return complete;
    }

    static bool IsWeak(TlsCipherSuite cipher) => cipher is TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA or TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA or TlsCipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA;
    sealed record Observation(bool Success, string Outcome, string? Explanation = null, bool Upgraded = false,
        SslProtocols Protocol = SslProtocols.None, TlsCipherSuite Cipher = default, SslPolicyErrors Validation = default)
    { public List<ToolSection> Sections { get; } = []; }

    async Task<Observation> Handshake(IPAddress address, int port, string identity, string mode, SslProtocols protocol, TlsCipherSuite? cipher, bool capture, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(network.ConnectionSeconds, 5)));
        var token = timeout.Token;
        var upgraded = false;
        X509Certificate2? leaf = null;
        var validation = SslPolicyErrors.None;
        var localChainRows = new List<ToolRow>();
        try
        {
            CipherSuitesPolicy? policy = null;
            if (cipher.HasValue)
            {
                if (!OperatingSystem.IsLinux()) return new(false, "Tool_TlsLocallyUntestable", "Tool_TlsCipherPlatformLimitation");
                policy = new CipherSuitesPolicy([cipher.Value]);
            }
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, token);
            var stream = client.GetStream();
            if (mode is "smtp" or "imap" or "pop3")
            {
                await Upgrade(stream, mode, token);
                upgraded = true;
            }
            using var tls = new SslStream(stream, false, (_, cert, chain, errors) =>
            {
                validation = errors;
                if (capture && cert is not null)
                {
                    leaf = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    if (chain is not null)
                    {
                        foreach (var element in chain.ChainElements) localChainRows.Add(new("Tool_CertificateChainElement", element.Certificate.Subject));
                        foreach (var status in chain.ChainStatus) localChainRows.Add(new("Tool_CertificateChainStatus", status.Status.ToString(), "warning"));
                    }
                }
                return true; // Inspection must retain invalid/expired peer certificates and report errors.
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = identity, EnabledSslProtocols = protocol, CipherSuitesPolicy = policy,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = new X509ChainPolicy { RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true }
            }, token);
            if (cipher.HasValue && tls.NegotiatedCipherSuite != cipher.Value)
                return new(false, "Tool_TlsLocallyUntestable", "Tool_TlsCipherNotHonored", upgraded);
            var result = new Observation(true, "Tool_Accepted", Upgraded: upgraded, Protocol: tls.SslProtocol, Cipher: tls.NegotiatedCipherSuite, Validation: validation);
            if (capture && leaf is not null)
            {
                result.Sections.Add(CertificateTools.Describe(leaf));
                result.Sections.Add(new("Tool_TlsPeerChain", [new("Tool_TlsWireLeaf", leaf.Subject), new("Tool_TlsWireChain", "Tool_TlsWireChainUnavailable", "info", "Tool_TlsWireChainLimitation")]));
                localChainRows.Insert(0, new("Tool_CertificateChainValidation", validation.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? "Tool_CertificateChainErrors" : "Tool_Accepted", validation.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? "warning" : "info", "Tool_CertificateChainNoRevocation"));
                result.Sections.Add(new("Tool_CertificateLocalChain", localChainRows));
            }
            return result;
        }
        catch (UpgradeRejected) { return new(false, "Tool_Rejected", "Tool_StartTlsRefused", upgraded); }
        catch (ProtocolFailure) { return new(false, "Tool_TlsInconclusive", "Tool_StartTlsMalformed", upgraded); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "Tool_TlsTimeout", Upgraded: upgraded); }
        catch (SocketException) { return new(false, "Tool_TlsUnreachable", Upgraded: upgraded); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        { return new(false, "Tool_TlsLocallyUntestable", "Tool_TlsLocalPolicyLimitation", upgraded); }
        catch (Exception ex) when (ex is AuthenticationException or IOException or System.Security.Cryptography.CryptographicException)
        { return new(false, "Tool_TlsInconclusive", "Tool_TlsProbeAmbiguous", upgraded); }
        finally { leaf?.Dispose(); }
    }

    sealed class UpgradeRejected : Exception;
    sealed class ProtocolFailure : Exception;

    static async Task Upgrade(NetworkStream stream, string mode, CancellationToken ct)
    {
        var bytes = 0;
        var lines = 0;
        async Task<string> Line()
        {
            if (++lines > 128) throw new ProtocolFailure();
            var data = new List<byte>(); var single = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(single, ct) == 0) throw new ProtocolFailure();
                if (++bytes > 32768 || data.Count > 2048) throw new ProtocolFailure();
                if (single[0] == 10)
                {
                    if (data.Count == 0 || data[^1] != 13) throw new ProtocolFailure();
                    return Encoding.ASCII.GetString(data.ToArray(), 0, data.Count - 1);
                }
                data.Add(single[0]);
            }
        }
        async Task Send(string command) => await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct);
        async Task<List<string>> Smtp(int expected)
        {
            var reply = new List<string>();
            while (true)
            {
                var line = await Line();
                if (line.Length < 4 || !int.TryParse(line.AsSpan(0, 3), out var code) || line[3] is not ('-' or ' ')) throw new ProtocolFailure();
                if (code != expected) throw new UpgradeRejected();
                reply.Add(line[4..]);
                if (line[3] == ' ') return reply;
            }
        }
        if (mode == "smtp")
        {
            await Smtp(220); await Send("EHLO diagnostic.invalid");
            var capabilities = await Smtp(250);
            if (!capabilities.Any(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) == true)) throw new UpgradeRejected();
            await Send("STARTTLS"); await Smtp(220);
        }
        else if (mode == "imap")
        {
            if (!(await Line()).StartsWith("* OK", StringComparison.OrdinalIgnoreCase)) throw new UpgradeRejected();
            await Send("a001 CAPABILITY"); var capable = false;
            while (true)
            {
                var line = await Line();
                if (line.StartsWith("a001 ", StringComparison.OrdinalIgnoreCase))
                { if (!line.StartsWith("a001 OK ", StringComparison.OrdinalIgnoreCase) || !capable) throw new UpgradeRejected(); break; }
                if (line.StartsWith("* CAPABILITY ", StringComparison.OrdinalIgnoreCase)) capable |= line.Split(' ').Contains("STARTTLS", StringComparer.OrdinalIgnoreCase);
            }
            await Send("a002 STARTTLS");
            while (true)
            {
                var line = await Line();
                if (line.StartsWith("* ", StringComparison.Ordinal)) continue;
                if (!line.StartsWith("a002 OK ", StringComparison.OrdinalIgnoreCase)) throw new UpgradeRejected();
                break;
            }
        }
        else
        {
            if (!(await Line()).StartsWith("+OK", StringComparison.OrdinalIgnoreCase)) throw new UpgradeRejected();
            await Send("CAPA");
            if (!(await Line()).StartsWith("+OK", StringComparison.OrdinalIgnoreCase)) throw new UpgradeRejected();
            var capable = false;
            while (true) { var line = await Line(); if (line == ".") break; capable |= line.Equals("STLS", StringComparison.OrdinalIgnoreCase); }
            if (!capable) throw new UpgradeRejected();
            await Send("STLS");
            if (!(await Line()).StartsWith("+OK", StringComparison.OrdinalIgnoreCase)) throw new UpgradeRejected();
        }
    }
}
