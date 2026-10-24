using System.Net.Mail;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Antiforgery;

namespace FullyQualified.Web;

public record EmailTestRequest(string? From, string? To, string? Subject, string? Message);

/// <summary>
/// Submits a real message to the server's configured relay, to see how a receiver scores it. Receiver-side authentication is
/// not inferred from SMTP acceptance. The sender is restricted to a domain this tenant monitors: without that check, this
/// endpoint would let any admin send mail that appears to come from an address they do not own, which is spoofing, not testing.
/// </summary>
public static class EmailTestEndpoints
{
    public const string LogChannel = "email-test";

    public static string? Validate(EmailTestRequest r)
    {
        if (!Address(r.From)) return "invalid_from";
        if (!Address(r.To)) return "invalid_to";
        if (string.IsNullOrWhiteSpace(r.Subject) || r.Subject.Length > 200 || r.Subject.Any(char.IsControl)) return "invalid_subject";
        if (string.IsNullOrWhiteSpace(r.Message) || r.Message.Length > 10000) return "invalid_message";
        return null;
    }

    static bool Address(string? value) => value is { Length: > 0 and <= 254 }
        && !value.Any(char.IsControl) && MailAddress.TryCreate(value.Trim(), out var address)
        && address.Address == value.Trim() && address.Host.Contains('.');

    /// <summary>True when <paramref name="fromHost"/> is, or is a subdomain of, a domain this tenant monitors.</summary>
    static async Task<bool> OwnsDomain(AppDbContext db, string fromHost, CancellationToken ct)
    {
        var host = fromHost.ToLowerInvariant();
        var domains = await db.Assets.Where(a => !a.Discovered && !a.Name.Contains(":")).Select(a => a.Name).ToListAsync(ct);
        return domains.Any(d => host == d.ToLowerInvariant() || host.EndsWith("." + d.ToLowerInvariant(), StringComparison.Ordinal));
    }

    public static void AddEmailTests(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("email-test", ctx => RateLimitPartition.GetFixedWindowLimiter(
            Roles.TenantId(ctx.User), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
    });

    public static void MapEmailTests(this WebApplication app) => app.MapPost("/admin/email-test", Send)
        .RequireAuthorization(Roles.Admin).RequireRateLimiting("email-test");

    static IResult Fail(int status, string error) => Results.Json(new { error }, statusCode: status);

    static async Task<IResult> Send(HttpContext ctx, EmailTestRequest request, AlertService alerts, AppDbContext db, IAntiforgery antiforgery)
    {
        if (Roles.TenantId(ctx.User) is not { }) return Results.Forbid();
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { return Fail(400, "antiforgery"); }
        if (Validate(request) is { } error) return Fail(400, error);
        if (!alerts.CanMail) return Fail(409, "no_smtp");
        // The sender must be a domain this tenant monitors: this is a self-test, not a way to send as anyone else's address.
        var fromHost = new MailAddress(request.From!.Trim()).Host;
        if (!await OwnsDomain(db, fromHost, ctx.RequestAborted)) return Fail(403, "from_not_owned");

        // Toolbox sending is transient: no input, delivery attempt or result is persisted.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await alerts.MailAsync(request.From!.Trim(), request.To!.Trim(), request.Subject!.Trim(), request.Message!, timeout.Token);
            return Results.Ok(new { accepted = true });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (Exception)
        {
            // SMTP replies can contain addresses and server details; never return or log them.
            return Fail(502, "smtp_failed");
        }
    }
}
