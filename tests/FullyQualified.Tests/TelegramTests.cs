using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullyQualified.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullyQualified.Tests;

public class TelegramFactory : PgFactory
{
    public static readonly int Port = Random.Shared.Next(49100, 49900);
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Alerts:Telegram:ApiBase", $"http://127.0.0.1:{Port}");
    }
}

public class TelegramTests(TelegramFactory factory) : OrgTestBase<TelegramFactory>(factory), IClassFixture<TelegramFactory>
{
    const string Token = "987654321:ZZsecretTOKENvalue0123456789abcdefgh";

    [Theory]
    [InlineData(Token, true)]
    [InlineData("123456789", false)]
    [InlineData("abc:ZZsecretTOKENvalue0123456789abcdefgh", false)]
    [InlineData("123456789:short", false)]
    [InlineData(null, false)]
    public void Bot_tokens_are_checked_for_shape(string? token, bool valid) => Assert.Equal(valid, AlertSettingsService.ValidTelegramToken(token));

    [Theory]
    [InlineData("123456789", true)]
    [InlineData("-1001234567890", true)]
    [InlineData("@mychannel", true)]
    [InlineData("@ab", false)]
    [InlineData("mychannel", false)]
    [InlineData("12 34", false)]
    public void Chat_ids_are_numbers_or_channel_names(string chat, bool valid) => Assert.Equal(valid, AlertSettingsService.ValidTelegramChat(chat));

    static object Settings(string? token, string? chat, bool clear = false) =>
        new { enabled = true, minStatus = "Warning", notifyRecovery = true, notifyDrift = true, telegramBotToken = token, telegramChatId = chat, clearTelegram = clear };

    [DbFact]
    public async Task Telegram_needs_both_a_token_and_a_chat_and_both_must_look_right()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/alerts/", Settings("nonsense", "123456789"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "not a chat"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(null, "123456789"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "-1001234567890"))).StatusCode);
    }

    [DbFact]
    public async Task The_token_is_stored_encrypted_and_only_the_bot_id_is_shown_again()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "-1001234567890"))).StatusCode);

        using (var scope = Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<AlertSettings>().SingleAsync(a => a.TenantId == org.Id);
            Assert.DoesNotContain("ZZsecret", row.TelegramBotToken);
            Assert.Equal("-1001234567890", row.TelegramChatId);
        }
        var page = await (await admin.GetAsync("/alerts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("ZZsecret", page);
        Assert.Contains("987654321", page);   // the bot id
        Assert.Contains("-1001234567890", page);
        Assert.Contains("How do I create a Telegram bot?", page);   // the how-to is on the page ...
        Assert.Contains("/newbot", page);
        Assert.Contains("getUpdates", page);

        // Leaving the token blank keeps it; the chat can change on its own.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings("", "-1009999999999"))).StatusCode);
        using (var scope = Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<AlertSettings>().SingleAsync(a => a.TenantId == org.Id);
            Assert.NotNull(row.TelegramBotToken);
            Assert.Equal("-1009999999999", row.TelegramChatId);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings("", "", clear: true))).StatusCode);
        using (var scope = Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AcrossTenants<AlertSettings>().SingleAsync(a => a.TenantId == org.Id);
            Assert.Null(row.TelegramBotToken);
            Assert.Null(row.TelegramChatId);
        }
    }

    [DbFact]
    public async Task A_test_alarm_reaches_the_Telegram_bot_api_with_the_chat_and_the_message()
    {
        string? path = null, body = null;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{TelegramFactory.Port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var c = await listener.GetContextAsync();
            path = c.Request.Url!.AbsolutePath;
            body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            c.Response.StatusCode = 200;
            c.Response.Close();
        });

        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "-1001234567890"))).StatusCode);
        var response = await admin.PostAsync("/admin/alerts/test", null);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("telegram", results[0].GetProperty("channel").GetString());
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.Equal($"/bot{Token}/sendMessage", path);
        var sent = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("-1001234567890", sent.GetProperty("chat_id").GetString());
        Assert.Contains("test alarm", sent.GetProperty("text").GetString());
    }

    [DbFact]
    public async Task A_refusal_from_Telegram_shows_its_reason()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{TelegramFactory.Port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var c = await listener.GetContextAsync();
            c.Response.StatusCode = 400;
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}");
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        });

        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "-1001234567890"))).StatusCode);
        var results = await (await admin.PostAsync("/admin/alerts/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.Equal("Telegram 400: Bad Request: chat not found", results[0].GetProperty("error").GetString());
        Assert.DoesNotContain("ZZsecret", results[0].GetRawText());
    }

    [DbFact]
    public async Task A_failed_delivery_is_logged_without_leaking_the_token()
    {
        var org = await NewOrg();
        var admin = Factory.As(org.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/alerts/", Settings(Token, "-1001234567890"))).StatusCode);
        var response = await admin.PostAsync("/admin/alerts/test", null); // nothing listens on the port now
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.DoesNotContain("ZZsecret", results[0].GetRawText());
    }
}
