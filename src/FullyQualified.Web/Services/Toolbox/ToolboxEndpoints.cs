using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Localization;

namespace FullyQualified.Web;

/// <summary>Only identity counters live beyond a request; observations and inputs never do.</summary>
public sealed class ToolLimits(IConfiguration config)
{
    sealed class Counter { public DateTimeOffset Started = DateTimeOffset.UtcNow; public int Requests; public int Active; }
    readonly Dictionary<string, Counter> users = [];
    readonly object gate = new();
    int active;

    public IDisposable? Enter(string identity)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in users.Where(p => p.Value.Active == 0 && now - p.Value.Started > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) users.Remove(key);
            if (!users.TryGetValue(identity, out var counter)) users[identity] = counter = new();
            if (now - counter.Started >= TimeSpan.FromMinutes(1)) { counter.Started = now; counter.Requests = 0; }
            if (counter.Requests >= Math.Clamp(config.GetValue("Toolbox:RequestsPerMinute", 30), 1, 1000)
                || counter.Active >= Math.Clamp(config.GetValue("Toolbox:ConcurrentPerUser", 4), 1, 32)
                || active >= Math.Clamp(config.GetValue("Toolbox:Concurrent", 16), 1, 128)) return null;
            counter.Requests++; counter.Active++; active++;
            return new Lease(() => { lock (gate) { counter.Active--; active--; } });
        }
    }

    sealed class Lease(Action release) : IDisposable
    {
        Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}

public static class ToolboxEndpoints
{
    public static readonly string[] Tools = ["tls", "starttls", "mx", "certificate", "dns", "spf", "dkim", "dmarc", "dmarc-builder", "headers", "mta-sts"];
    const int MaxBody = 448 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static IServiceCollection AddToolbox(this IServiceCollection services) => services
        .AddSingleton<ToolNetwork>().AddSingleton<ToolLimits>().AddTransient<TlsTools>()
        .AddTransient<MailTools>().AddTransient<SpfTools>();

    public static void MapToolbox(this WebApplication app) => app.MapPost("/api/toolbox", Execute).RequireAuthorization();

    public static bool IsPrivatePage(PathString path) => path.StartsWithSegments("/toolbox") || path.StartsWithSegments("/api/toolbox")
        || path.StartsWithSegments("/email-test") || path.StartsWithSegments("/admin/email-test");

    static async Task<IResult> Execute(HttpContext ctx, IAntiforgery antiforgery, ToolLimits limits,
        TlsTools tls, MailTools mail, SpfTools spf, IConfiguration config, IStringLocalizer<SharedResource> l)
    {
        IResult Fail(int status, string code)
        {
            var message = l["Tool_Error_" + code];
            return Results.Json(new { error = message.ResourceNotFound ? l["Tool_Error_invalid_input"].Value : message.Value }, statusCode: status);
        }
        // Reject form uploads before antiforgery can invoke form buffering or temporary files.
        if (ctx.Request.ContentLength > MaxBody) return Fail(413, "too_large");
        if (!ctx.Request.HasJsonContentType()) return Fail(415, "invalid_input");
        if (!ctx.Request.Headers.ContainsKey("RequestVerificationToken")) return Fail(400, "antiforgery");
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { return Fail(400, "antiforgery"); }
        using var permit = limits.Enter(Roles.UserId(ctx.User)?.ToString() ?? ctx.User.Identity?.Name ?? "authenticated");
        if (permit is null) return Fail(429, "rate");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Toolbox:RunSeconds", 60), 5, 120)));
        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var n = await ctx.Request.Body.ReadAsync(bytes, timeout.Token);
                if (n == 0) break;
                if (buffer.Length + n > MaxBody) return Fail(413, "too_large");
                await buffer.WriteAsync(bytes.AsMemory(0, n), timeout.Token);
            }
            var input = JsonSerializer.Deserialize<ToolRequest>(buffer.ToArray(), JsonOptions);
            if (input is null || !Tools.Contains(input.Tool)) return Fail(400, "invalid_input");
            if (input.Text is { } text && Encoding.UTF8.GetByteCount(text) > 64 * 1024
                || input.FileBase64?.Length > 4 * ((256 * 1024 + 2) / 3)) return Fail(413, "too_large");
            if (input.Port is < 1 or > 65535 || input.Percentage is < 0 or > 100) return Fail(400, "invalid_input");
            string?[] small = [input.Host, input.ServerName, input.Mode, input.Domain, input.Selector, input.RecordType,
                input.SenderIp, input.EnvelopeFrom, input.Helo, input.Policy, input.SubdomainPolicy, input.AlignmentSpf, input.AlignmentDkim];
            if (input.ReportUri?.Length > 4096 || input.ReportUri?.Any(char.IsControl) == true) return Fail(400, "invalid_input");
            if (small.Any(s => s?.Length > 1024 || s?.Any(char.IsControl) == true)) return Fail(400, "invalid_input");
            var result = input.Tool switch
            {
                "tls" or "starttls" or "mx" => await tls.RunAsync(input, timeout.Token),
                "certificate" => CertificateTools.Inspect(input),
                "headers" => HeaderTools.Analyze(input.Text ?? ""),
                "spf" => await spf.RunAsync(input, timeout.Token),
                _ => await mail.RunAsync(input, timeout.Token)
            };
            string Localize(string key) => l[key].Value;
            return Results.Json(result with { Sections = result.Sections.Select(section => section with
            {
                Title = Localize(section.Title),
                Rows = section.Rows.Select(row => row with { Label = Localize(row.Label),
                    Value = row.Value.StartsWith("Tool_", StringComparison.Ordinal) ? Localize(row.Value) : row.Value,
                    Explanation = row.Explanation is null ? null : Localize(row.Explanation) }).ToList()
            }).ToList() });
        }
        catch (JsonException) { return Fail(400, "invalid_input"); }
        catch (ToolException ex) { return Fail(ex.Code == "too_large" ? 413 : 400, ex.Code); }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new ToolResult([new(l["Tool_Incomplete"], [new(l["Tool_Status"], l["Tool_Timeout"], "unknown")])], false));
        }
        catch (OperationCanceledException) { return Results.StatusCode(499); }
        catch (Exception) { return Fail(502, "failed"); } // Do not log exceptions containing supplied targets or material.
    }
}
