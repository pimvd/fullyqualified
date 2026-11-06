using FullyQualified.Web;

namespace FullyQualified.Tests;

public class ExpiryTimelineTests
{
    [Theory]
    [InlineData(-5, 3)]
    [InlineData(0, 3)]
    [InlineData(90, 97)]
    [InlineData(400, 97)]
    public void Position_stays_on_the_axis(int days, double expected) => Assert.Equal(expected, ExpiryTimeline.Position(days), 6);

    [Fact]
    public void Position_grows_with_the_days_left() => Assert.True(ExpiryTimeline.Position(10) < ExpiryTimeline.Position(60));

    [Theory]
    [InlineData(-3, "bad")]
    [InlineData(7, "bad")]
    [InlineData(8, "warn")]
    [InlineData(30, "warn")]
    [InlineData(31, "ok")]
    public void Severity_follows_the_thresholds(int days, string expected) => Assert.Equal(expected, ExpiryTimeline.Severity(days));
}
