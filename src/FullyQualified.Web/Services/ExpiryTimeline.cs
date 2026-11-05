namespace FullyQualified.Web;

/// <summary>Where a certificate sits on the dashboard's expiry timeline, and how urgent it looks.</summary>
public static class ExpiryTimeline
{
    public const int Days = 90;

    /// <summary>Horizontal position in percent. Expired and far-off certificates stay on the axis, with a margin so labels are not cut off.</summary>
    public static double Position(int daysLeft) => Math.Clamp(daysLeft, 0, Days) * 100.0 / Days * 0.94 + 3;

    /// <summary>"bad" within a week or once expired, "warn" within 30 days, otherwise "ok". Each has its own marker shape.</summary>
    public static string Severity(int daysLeft) => daysLeft <= 7 ? "bad" : daysLeft <= 30 ? "warn" : "ok";
}
