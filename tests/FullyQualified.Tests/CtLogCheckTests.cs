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
