using System.Globalization;

namespace Beachtro.Domain.Calendar;

/// <summary>
/// Business-date policy (D-06). The business date of an instant is the local date in the policy
/// time zone after subtracting the cutoff, so a 04:00 cutoff puts 03:30 sales on the previous day.
/// Dates are derived once and snapshotted; history is never re-derived under a newer policy.
/// </summary>
public sealed record CalendarPolicy
{
    public const int MinutesPerDay = 24 * 60;

    public CalendarPolicy(string timeZoneId, int cutoffMinute)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("A time zone is required.", nameof(timeZoneId));
        }

        if (cutoffMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffMinute), "Cutoff must be 0..1439 minutes after local midnight.");
        }

        TimeZone = ResolveTimeZone(timeZoneId);
        TimeZoneId = timeZoneId;
        CutoffMinute = cutoffMinute;
    }

    public string TimeZoneId { get; }
    public int CutoffMinute { get; }
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Provisional B-01 default: Asia/Manila, midnight boundary.</summary>
    public static CalendarPolicy ManilaMidnight { get; } = new("Asia/Manila", 0);

    /// <summary>ISO YYYY-MM-DD business date for a UTC instant.</summary>
    public string BusinessDateFor(long utcMilliseconds)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMilliseconds);
        var local = TimeZoneInfo.ConvertTime(utc, TimeZone);
        return DateOnly.FromDateTime(local.AddMinutes(-CutoffMinute).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static bool IsKnownTimeZone(string timeZoneId)
    {
        try
        {
            ResolveTimeZone(timeZoneId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            // IANA IDs (e.g. Asia/Manila) resolve on Windows through ICU.
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Unknown time zone '{id}'.", nameof(id), ex);
        }
    }
}
