using Beachtro.Domain.Calendar;

namespace Beachtro.Domain.Tests.Calendar;

/// <summary>D-06 business dates. Manila is UTC+8 with no daylight saving.</summary>
public sealed class BusinessCalendarTests
{
    private static long Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("2026-11-16T15:59:59.999Z", "2026-11-16")] // 23:59:59.999 Manila
    [InlineData("2026-11-16T16:00:00.000Z", "2026-11-17")] // 00:00 Manila
    [InlineData("2026-11-15T16:00:00.000Z", "2026-11-16")]
    [InlineData("2026-12-31T16:00:00.000Z", "2027-01-01")]
    public void Midnight_boundary_uses_manila_local_time(string utc, string expected)
    {
        Assert.Equal(expected, CalendarPolicy.ManilaMidnight.BusinessDateFor(Utc(utc)));
    }

    [Theory]
    [InlineData("2026-11-16T19:30:00Z", "2026-11-16")] // 03:30 on the 17th local, before a 04:00 cutoff
    [InlineData("2026-11-16T20:00:00Z", "2026-11-17")] // 04:00 local
    public void Cutoff_moves_early_morning_sales_to_the_previous_business_day(string utc, string expected)
    {
        Assert.Equal(expected, new CalendarPolicy("Asia/Manila", 240).BusinessDateFor(Utc(utc)));
    }

    [Fact]
    public void Invalid_zone_and_cutoff_are_rejected()
    {
        Assert.False(CalendarPolicy.IsKnownTimeZone("Mars/Olympus"));
        Assert.True(CalendarPolicy.IsKnownTimeZone("Asia/Manila"));
        Assert.Throws<ArgumentException>(() => new CalendarPolicy("Mars/Olympus", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalendarPolicy("Asia/Manila", 1440));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalendarPolicy("Asia/Manila", -1));
    }

    [Theory]
    [InlineData(0, "2026-11-16", "2026-11-15T16:00:00Z")]
    [InlineData(240, "2026-11-16", "2026-11-15T20:00:00Z")]
    public void Start_of_business_date_is_local_midnight_plus_cutoff(int cutoff, string date, string expectedUtc)
    {
        var policy = new CalendarPolicy("Asia/Manila", cutoff);
        var start = policy.StartUtcOf(date);
        Assert.Equal(Utc(expectedUtc), start);
        Assert.Equal(date, policy.BusinessDateFor(start));
        Assert.NotEqual(date, policy.BusinessDateFor(start - 1));
    }

    [Fact]
    public void Local_time_text_uses_the_policy_zone_not_the_pc()
    {
        Assert.Equal("2026-11-17 00:05", CalendarPolicy.ManilaMidnight.LocalTimeText(Utc("2026-11-16T16:05:00Z")));
        Assert.Throws<FormatException>(() => CalendarPolicy.ManilaMidnight.StartUtcOf("16/11/2026"));
    }
}
