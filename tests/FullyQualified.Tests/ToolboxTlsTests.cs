using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;
using DnsClient.Protocol;
using FullyQualified.Web;
using Microsoft.Extensions.Configuration;

namespace FullyQualified.Tests;

public class ToolboxTlsTests
{
    static IConfiguration Config(bool allowPrivate = true) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Scanning:AllowPrivateAddresses"] = allowPrivate.ToString(), ["Toolbox:ConnectionSeconds"] = "1" }).Build();
    static X509Certificate2 Certificate(bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("fixture.example"); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 3));
    }
    static IEnumerable<ToolRow> Rows(ToolResult result) => result.Sections.SelectMany(s => s.Rows);

    [Fact]
    public void Certificate_inspection_accepts_der_and_pem_and_reports_expiry_and_names()
    {
        using var cert = Certificate(true);
        var pem = CertificateTools.Inspect(new("certificate", Text: cert.ExportCertificatePem()));
        var der = CertificateTools.Inspect(new("certificate", FileBase64: Convert.ToBase64String(cert.RawData)));
        Assert.Contains(Rows(pem), r => r.Label == "Tool_CertificateExpired" && r.Status == "warning");
        Assert.Contains(Rows(pem), r => r.Value.Contains("fixture.example"));
        Assert.Equal(Rows(pem).Single(r => r.Label == "Tool_CertificateFingerprint").Value,
            Rows(der).Single(r => r.Label == "Tool_CertificateFingerprint").Value);
        Assert.Contains(pem.Sections, s => s.Title == "Tool_CertificateLocalChain");
    }

    [Fact]
    public void Certificate_inspection_rejects_private_keys_even_beside_a_certificate()
    {
        using var cert = Certificate(); using var key = RSA.Create();
        var ex = Assert.Throws<ToolException>(() => CertificateTools.Inspect(new("certificate", Text: cert.ExportCertificatePem() + key.ExportPkcs8PrivateKeyPem())));
        Assert.Equal("private_key", ex.Code);
    }

    [Fact]
    public void Certificate_upload_rejects_binary_private_keys_and_pkcs12_containers()
    {
        using var key = RSA.Create(2048); using var cert = Certificate();
        foreach (var data in new[] { key.ExportPkcs8PrivateKey(), cert.Export(X509ContentType.Pfx) })
            Assert.Throws<ToolException>(() => CertificateTools.Inspect(new("certificate", FileBase64: Convert.ToBase64String(data))));
    }

    [Fact]
    public void Certificate_paste_and_upload_limits_are_enforced_before_parsing()
    {
        Assert.Equal("too_large", Assert.Throws<ToolException>(() => CertificateTools.Inspect(new("certificate", Text: new string('x', 65537)))).Code);
        Assert.Equal("too_large", Assert.Throws<ToolException>(() => CertificateTools.Inspect(new("certificate", FileBase64: Convert.ToBase64String(new byte[262145])))).Code);
    }

    [Fact]
    public async Task Tls_inspection_preserves_sni_and_observes_expired_certificates()
    {
        using var cert = Certificate(true);
        await using var fixture = new Fixture(cert);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new TlsTools(new ToolNetwork(Config()), Config()).RunAsync(
            new("tls", Host: "127.0.0.1", Port: fixture.Port, ServerName: "fixture.example"), timeout.Token);
        Assert.Equal("fixture.example", fixture.ServerName);
        Assert.Contains(Rows(result), r => r.Label == "Tool_CertificateExpired" && r.Status == "warning");
        Assert.Contains(Rows(result), r => r.Label == "Tool_TlsValidation" && r.Value.Contains("RemoteCertificateChainErrors"));
        Assert.Contains(Rows(result), r => r.Label == "Tool_TlsCipherCandidates");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smtp_multiline_replies_require_positive_starttls(bool accept)
    {
        using var cert = Certificate();
        await using var fixture = new Fixture(cert, smtp: true, acceptUpgrade: accept);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new TlsTools(new ToolNetwork(Config()), Config()).RunAsync(
            new("starttls", Host: "127.0.0.1", Port: fixture.Port, ServerName: "fixture.example", Mode: "smtp"), timeout.Token);
        Assert.Contains(Rows(result), r => r.Label == "Tool_StartTlsUpgrade" && r.Value == (accept ? "Tool_Accepted" : "Tool_Rejected"));
        Assert.Equal(accept, Rows(result).Any(r => r.Label == "Tool_CertificateFingerprint"));
    }

    [Theory]
    [InlineData("imap")]
    [InlineData("pop3")]
    [InlineData("smtps")]
    [InlineData("imaps")]
    [InlineData("pop3s")]
    public async Task Mail_protocols_upgrade_or_connect_implicitly_without_authentication(string mode)
    {
        using var cert = Certificate();
        await using var fixture = new Fixture(cert, mailMode: mode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new TlsTools(new ToolNetwork(Config()), Config()).RunAsync(
            new("starttls", Host: "127.0.0.1", Port: fixture.Port, ServerName: "fixture.example", Mode: mode), timeout.Token);
        Assert.Contains(Rows(result), r => r.Label == "Tool_CertificateFingerprint");
        Assert.True(fixture.UpgradeCommandsValid);
    }

    [Fact]
    public void Pem_chain_inspection_uses_submitted_intermediates_and_labels_local_chain()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=fixture root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(4));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=fixture.example", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var leaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2), new byte[] { 1, 2, 3 });
        var result = CertificateTools.Inspect(new("certificate", Text: leaf.ExportCertificatePem() + "\n" + root.ExportCertificatePem()));
        Assert.Equal(2, result.Sections.Count(s => s.Title == "Tool_CertificateDetails"));
        var chain = Assert.Single(result.Sections, s => s.Title == "Tool_CertificateLocalChain");
        Assert.Contains(chain.Rows, r => r.Label == "Tool_CertificateChainElement" && r.Value == "CN=fixture root");
    }

    [Theory]
    [InlineData("not a certificate")]
    [InlineData("-----BEGIN CERTIFICATE-----\ninvalid\n-----END CERTIFICATE-----")]
    [InlineData("garbage before\n")]
    public void Certificate_inspection_rejects_malformed_material(string text)
    {
        using var cert = Certificate();
        if (text.StartsWith("garbage")) text += cert.ExportCertificatePem();
        Assert.Equal("invalid_certificate", Assert.Throws<ToolException>(() => CertificateTools.Inspect(new("certificate", Text: text))).Code);
    }

    [Fact]
    public async Task A_fake_resolver_cannot_bypass_policy_for_a_secondary_address()
    {
        var config = Config(false);
        var ex = await Assert.ThrowsAsync<ToolException>(() => new TlsTools(new FakeNetwork(config), config)
            .RunAsync(new("tls", Host: "fixture.example"), CancellationToken.None));
        Assert.Equal("blocked", ex.Code);
    }

    [Fact]
    public async Task Cancellation_stops_waiting_for_an_incomplete_greeting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var result = await new TlsTools(new ToolNetwork(Config()), Config()).RunAsync(
            new("starttls", Host: "127.0.0.1", Port: ((IPEndPoint)listener.LocalEndpoint).Port, Mode: "smtp"), timeout.Token);
        Assert.False(result.Complete);
        Assert.Contains(Rows(result), r => r.Label == "Tool_TlsOutcome" && r.Value == "Tool_TlsRunStopped");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mx_discovery_reports_empty_and_null_mx_without_connecting(bool nullMx)
    {
        var config = Config();
        var network = new MxNetwork(config, nullMx ? [new MxRecord(new ResourceRecordInfo("fixture.example", ResourceRecordType.MX, QueryClass.IN, 60, 0), 0, DnsString.Parse("."))] : []);
        var result = await new TlsTools(network, config).RunAsync(new("mx", Domain: "fixture.example"), CancellationToken.None);
        Assert.Contains(Rows(result), r => r.Value == (nullMx ? "Tool_NullMx" : "Tool_NoMx"));
    }

    [Fact]
    public async Task Truncated_mx_is_not_reported_as_an_empty_answer()
    {
        var config = Config();
        var ex = await Assert.ThrowsAsync<ToolException>(() => new TlsTools(new MxNetwork(config, [], true), config)
            .RunAsync(new("mx", Domain: "fixture.example"), CancellationToken.None));
        Assert.Equal("dns_failed", ex.Code);
    }

    [Fact]
    public async Task Mx_requires_a_domain_instead_of_an_ip_address()
    {
        var config = Config();
        var ex = await Assert.ThrowsAsync<ToolException>(() => new TlsTools(new MxNetwork(config, []), config)
            .RunAsync(new("mx", Domain: "127.0.0.1"), CancellationToken.None));
        Assert.Equal("invalid_host", ex.Code);
    }

    sealed class MxNetwork(IConfiguration config, DnsResourceRecord[] records, bool truncated = false) : ToolNetwork(config)
    {
        public override Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct) => Task.FromResult<IDnsQueryResponse>(new Response(records, truncated));
        public override Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => throw new InvalidOperationException("No connection was expected.");
    }
    sealed class Response(DnsResourceRecord[] answers, bool truncated) : IDnsQueryResponse
    {
        public IReadOnlyList<DnsQuestion> Questions => [];
        public IReadOnlyList<DnsResourceRecord> Additionals => [];
        public IEnumerable<DnsResourceRecord> AllRecords => Answers;
        public IReadOnlyList<DnsResourceRecord> Answers => answers;
        public IReadOnlyList<DnsResourceRecord> Authorities => [];
        public string AuditTrail => "";
        public string ErrorMessage => "";
        public bool HasError => false;
        public DnsResponseHeader Header => new(1, (ushort)(truncated ? 0x8200 : 0x8000), 1, answers.Length, 0, 0);
        public int MessageSize => 0;
        public NameServer NameServer => new(IPAddress.Parse("192.0.2.53"));
        public DnsQuerySettings Settings => new DnsQueryOptions();
    }

    sealed class FakeNetwork(IConfiguration config) : ToolNetwork(config)
    {
        public override Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback });
        public override Task GuardAddressesAsync(IEnumerable<IPAddress> addresses, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task loop;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public string? ServerName { get; private set; }
        public bool UpgradeCommandsValid { get; private set; } = true;
        public Fixture(X509Certificate2 cert, bool smtp = false, bool acceptUpgrade = true, string? mailMode = null)
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(stop.Token);
                        _ = Serve(client, cert, smtp, acceptUpgrade, mailMode);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        async Task Serve(TcpClient client, X509Certificate2 cert, bool smtp, bool acceptUpgrade, string? mailMode)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                if (smtp)
                {
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    async Task Send(string value) => await stream.WriteAsync(Encoding.ASCII.GetBytes(value), stop.Token);
                    await Send("220-fixture\r\n220 ready\r\n");
                    Assert.StartsWith("EHLO ", await reader.ReadLineAsync(stop.Token));
                    await Send("250-fixture\r\n250-SIZE 1000\r\n250 STARTTLS\r\n");
                    Assert.Equal("STARTTLS", await reader.ReadLineAsync(stop.Token));
                    await Send(acceptUpgrade ? "220 begin TLS\r\n" : "454 unavailable\r\n");
                    if (!acceptUpgrade) return;
                }
                if (mailMode is "imap" or "pop3")
                {
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    async Task Send(string value) => await stream.WriteAsync(Encoding.ASCII.GetBytes(value), stop.Token);
                    if (mailMode == "imap")
                    {
                        await Send("* OK fixture ready\r\n");
                        UpgradeCommandsValid &= await reader.ReadLineAsync(stop.Token) == "a001 CAPABILITY";
                        await Send("* CAPABILITY IMAP4rev1 STARTTLS\r\na001 OK complete\r\n");
                        UpgradeCommandsValid &= await reader.ReadLineAsync(stop.Token) == "a002 STARTTLS";
                        await Send("* OK upgrade pending\r\na002 OK begin TLS\r\n");
                    }
                    else
                    {
                        await Send("+OK fixture ready\r\n");
                        UpgradeCommandsValid &= await reader.ReadLineAsync(stop.Token) == "CAPA";
                        await Send("+OK capabilities\r\nSTLS\r\nUIDL\r\n.\r\n");
                        UpgradeCommandsValid &= await reader.ReadLineAsync(stop.Token) == "STLS";
                        await Send("+OK begin TLS\r\n");
                    }
                }
                using var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { ServerName = name; return cert; },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, stop.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { await stop.CancelAsync(); listener.Stop(); await loop; stop.Dispose(); }
    }
}
