using System.Net;
using FullyQualified.Web;

namespace FullyQualified.Tests;

public class CtLogCheckTests
{
    class Handler(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    class Factory(Handler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    static readonly string CertSpotterBody = $$"""
        [{"id":"1","cert_sha256":"abc","dns_names":["www.example.com","*.example.com","example.com"],
          "issuer":{"name":"C=US, O=Let's Encrypt, CN=R11"},"not_before":"2026-10-01T00:00:00Z","not_after":"{{DateTime.UtcNow.AddDays(60):yyyy-MM-ddTHH:mm:ssZ}}"},
         {"id":"2","cert_sha256":"old","dns_names":["gone.example.com"],
          "issuer":{"name":"CN=R10"},"not_before":"2020-01-01T00:00:00Z","not_after":"2021-01-01T00:00:00Z"}]
        """;

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Falls_back_to_Cert_Spotter_when_crt_sh_fails(HttpStatusCode crtShStatus)
    {
        var handler = new Handler(u => u.Host == "crt.sh" ? new HttpResponseMessage(crtShStatus) : Json(CertSpotterBody));
        var outcome = await new CtLogCheck(new Factory(handler)).RunAsync(new Asset { Name = "example.com" }, default);

        Assert.Equal(["crt.sh", "api.certspotter.com"], handler.Hosts);
        Assert.Equal(CheckStatus.Ok, outcome.Status);
        Assert.Equal("Sum_CtOk|1|1", outcome.Summary);          // expired and wildcard entries are ignored
        var host = Assert.Single(outcome.DiscoveredHosts!);
        Assert.Equal("www.example.com", host.Name);
        Assert.Equal("Cert Spotter", host.Service);
        Assert.Equal("R11", host.Issuer);
        Assert.Equal("R11", outcome.Certs!.Single().Issuer);
        Assert.Equal("Automated", outcome.Certs!.Single().Renewal);
    }

    [Fact]
    public async Task Does_not_contact_Cert_Spotter_when_crt_sh_works()
    {
        var handler = new Handler(_ => Json("[]"));
        var outcome = await new CtLogCheck(new Factory(handler)).RunAsync(new Asset { Name = "example.com" }, default);
        Assert.Equal(["crt.sh"], handler.Hosts);
        Assert.Equal(CheckStatus.Ok, outcome.Status);
    }

    [Fact]
    public async Task Reports_an_error_naming_both_services_when_both_fail()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var outcome = await new CtLogCheck(new Factory(handler)).RunAsync(new Asset { Name = "example.com" }, default);
        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.StartsWith("Sum_Error|crt.sh:", outcome.Summary);
        Assert.Contains("Cert Spotter:", outcome.Summary);
    }
}

public class CtLogCheckCapTests
{
    /// <summary>A body whose bytes never arrive: it only ends when the token is cancelled.</summary>
    sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        }
    }

    class Factory(Handler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    [Fact]
    public async Task A_response_body_that_never_finishes_is_cut_off_by_the_job_cap_and_does_not_fall_back_to_Cert_Spotter()
    {
        var handler = new Handler();
        using var cap = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var run = new CtLogCheck(new Factory(handler)).RunAsync(new Asset { Name = "example.com" }, cap.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, handler.Calls); // the cap is the job's, not a fetch error: no second request
    }
}
