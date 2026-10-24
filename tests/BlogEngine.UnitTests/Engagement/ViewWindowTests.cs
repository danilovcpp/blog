using BlogEngine.Core.Engagement.Domain;

namespace BlogEngine.UnitTests.Engagement;

public sealed class ViewWindowTests
{
    [Fact]
    public void WindowIsExactlyTwentyFourHoursAndIncludesEquality()
    {
        var last = DateTimeOffset.Parse("2026-10-24T23:59:59Z", System.Globalization.CultureInfo.InvariantCulture);
        var due = last + ViewDeduplicationRecord.CountingWindow;
        Assert.True(last.AddSeconds(2) < due);
        Assert.True(last.AddHours(24).AddTicks(-1) < due);
        Assert.Equal(last.AddHours(24), due);
        Assert.True(last.AddHours(24).AddTicks(1) > due);
    }
}
