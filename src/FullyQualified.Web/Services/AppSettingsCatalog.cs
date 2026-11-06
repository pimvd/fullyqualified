namespace FullyQualified.Web;

public enum SettingKind { Text, Number, Bool, Secret, Choice }

/// <summary>
/// One server setting the platform owner can change in the database. <see cref="Default"/> is what the code uses when nothing sets the key, shown so
/// the page can say what "not set" means. <see cref="Restart"/>: read once at startup, so a change applies after the next restart.
/// </summary>
public record SettingDef(string Key, string Group, SettingKind Kind, string Default = "", bool Restart = false, string[]? Choices = null, double Min = double.NegativeInfinity, double Max = double.PositiveInfinity)
{
    /// <summary>The resource name of the label: "Alerts:Email:Host" becomes "Setting_Alerts_Email_Host".</summary>
    public string ResourceKey => "Setting_" + Key.Replace(':', '_');
}

/// <summary>
/// Every setting that lives in the database. Left out on purpose: <c>ConnectionStrings:Default</c> (the database itself), <c>Hosting:Role</c> (chosen before
/// the host exists, and the worker host has no UI) and <c>Database:AutoMigrate</c> (decides whether the schema exists at all). Those stay in the environment.
/// </summary>
public static class AppSettingsCatalog
{
    public static readonly string[] Groups = ["Appearance", "Email", "Alarms", "Access", "Sso", "Scanning", "Dns", "PublicCheck", "Toolbox"];

    public static readonly IReadOnlyList<SettingDef> All =
    [
        new("Appearance:Theme", "Appearance", SettingKind.Choice, "default", Choices: ["default", "christmas"]),

        new("Alerts:Email:Host", "Email", SettingKind.Text),
        new("Alerts:Email:Port", "Email", SettingKind.Number, "587", Min: 1, Max: 65535),
        new("Alerts:Email:Ssl", "Email", SettingKind.Bool, "true"),
        new("Alerts:Email:User", "Email", SettingKind.Text),
        new("Alerts:Email:Password", "Email", SettingKind.Secret),
        new("Alerts:Email:From", "Email", SettingKind.Text, "fullyqualified@localhost"),
        new("Alerts:Email:To", "Email", SettingKind.Text),

        new("Alerts:SlackWebhook", "Alarms", SettingKind.Secret),
        new("Alerts:Webhook", "Alarms", SettingKind.Secret),
        new("Alerts:Telegram:BotToken", "Alarms", SettingKind.Secret),
        new("Alerts:Telegram:ChatId", "Alarms", SettingKind.Text),

        new("Auth:Origin", "Access", SettingKind.Text),
        new("Auth:RpId", "Access", SettingKind.Text),
        new("Auth:SetupCode", "Access", SettingKind.Secret),

        new("Sso:Authority", "Sso", SettingKind.Text, Restart: true),
        new("Sso:ClientId", "Sso", SettingKind.Text, Restart: true),
        new("Sso:ClientSecret", "Sso", SettingKind.Secret, Restart: true),
        new("Sso:DisplayName", "Sso", SettingKind.Text, "SSO", Restart: true),
        new("Sso:AllowedDomains", "Sso", SettingKind.Text),
        new("Sso:AutoProvision", "Sso", SettingKind.Bool, "false"),
        new("Sso:DefaultRole", "Sso", SettingKind.Choice, "Viewer", Choices: ["Viewer", "Member"]),
        new("Sso:RequireHttpsMetadata", "Sso", SettingKind.Bool, "true", Restart: true),

        new("Scanning:Enabled", "Scanning", SettingKind.Bool, "true", Restart: true),
        new("Scanning:Workers", "Scanning", SettingKind.Number, "4", Restart: true, Min: 0, Max: 64),
        new("Scanning:IntervalHours", "Scanning", SettingKind.Number, "6", Min: 0.0166, Max: 720),
        new("Scanning:ManualCooldownMinutes", "Scanning", SettingKind.Number, "5", Min: 0, Max: 1440),
        new("Scanning:JobTimeoutSeconds", "Scanning", SettingKind.Number, "90", Min: 1, Max: 3600),
        new("Scanning:AllowPrivateAddresses", "Scanning", SettingKind.Bool, "true", Restart: true),

        new("Dns:Server", "Dns", SettingKind.Text, Restart: true),
        new("Dns:CompareResolvers", "Dns", SettingKind.Text, "1.1.1.1,9.9.9.9,8.8.8.8", Restart: true),

        new("PublicScan:Enabled", "PublicCheck", SettingKind.Bool, "false"),
        new("PublicScan:PerHour", "PublicCheck", SettingKind.Number, "10", Min: 1, Max: 100000),
        new("PublicScan:Concurrent", "PublicCheck", SettingKind.Number, "8", Restart: true, Min: 1, Max: 256),

        new("Toolbox:RequestsPerMinute", "Toolbox", SettingKind.Number, "30", Min: 1, Max: 1000),
        new("Toolbox:ConcurrentPerUser", "Toolbox", SettingKind.Number, "4", Min: 1, Max: 32),
        new("Toolbox:Concurrent", "Toolbox", SettingKind.Number, "16", Min: 1, Max: 128),
        new("Toolbox:RunSeconds", "Toolbox", SettingKind.Number, "60", Min: 5, Max: 120),
        new("Toolbox:ConnectionSeconds", "Toolbox", SettingKind.Number, "5", Min: 1, Max: 30),
        new("Toolbox:MaxAddresses", "Toolbox", SettingKind.Number, "8", Min: 1, Max: 32),
        new("Toolbox:MaxMxHosts", "Toolbox", SettingKind.Number, "10", Min: 1, Max: 30),
        new("Toolbox:MaxPolicyBytes", "Toolbox", SettingKind.Number, "262144", Min: 1024, Max: 1048576),
    ];

    public static SettingDef? Find(string? key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the value is acceptable for the setting, otherwise a short error code. Empty means "empty on purpose" and is allowed for text.</summary>
    public static string? Validate(SettingDef def, string value)
    {
        switch (def.Kind)
        {
            case SettingKind.Number:
                return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= def.Min && n <= def.Max ? null : "invalid_number";
            case SettingKind.Bool:
                return value is "true" or "false" ? null : "invalid_bool";
            case SettingKind.Choice:
                return def.Choices!.Contains(value) ? null : "invalid_choice";
            default:
                return value.Length <= 500 ? null : "too_long";
        }
    }
}
