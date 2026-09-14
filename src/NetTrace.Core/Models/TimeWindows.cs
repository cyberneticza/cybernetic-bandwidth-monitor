namespace NetTrace.Core.Models;

/// <summary>How far back the trends overview reaches. Not persisted; safe to reorder.</summary>
public enum TimeWindow
{
    Week1,
    Week2,
    Month1,
    Month3,
    Month6,
    Year1
}

public static class TimeWindowExtensions
{
    public static int Days(this TimeWindow window) => window switch
    {
        TimeWindow.Week1 => 7,
        TimeWindow.Week2 => 14,
        TimeWindow.Month1 => 30,
        TimeWindow.Month3 => 91,
        TimeWindow.Month6 => 182,
        TimeWindow.Year1 => 365,
        _ => throw new ArgumentOutOfRangeException(nameof(window))
    };

    public static string Label(this TimeWindow window) => window switch
    {
        TimeWindow.Week1 => "Last 7 days",
        TimeWindow.Week2 => "Last 14 days",
        TimeWindow.Month1 => "Last 30 days",
        TimeWindow.Month3 => "Last 3 months",
        TimeWindow.Month6 => "Last 6 months",
        TimeWindow.Year1 => "Last year",
        _ => window.ToString()
    };

    public static TimeSpan BucketSize(this Granularity granularity) => granularity switch
    {
        Granularity.Minute => TimeSpan.FromMinutes(1),
        Granularity.Hour => TimeSpan.FromHours(1),
        Granularity.Day => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    };

    public static long BucketSeconds(this Granularity granularity) => granularity switch
    {
        Granularity.Minute => 60L,
        Granularity.Hour => 3_600L,
        Granularity.Day => 86_400L,
        _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    };

    /// <summary>Truncates to the start of the containing bucket, in UTC.</summary>
    public static DateTimeOffset FloorTo(this DateTimeOffset value, Granularity granularity)
    {
        long seconds = value.ToUnixTimeSeconds();
        long size = granularity.BucketSeconds();
        long remainder = seconds % size;
        if (remainder < 0) remainder += size;   // stay correct for pre-epoch values
        return DateTimeOffset.FromUnixTimeSeconds(seconds - remainder);
    }
}

/// <summary>
/// Local calendar days as UTC instants. Charts and the "today" total are about the user's days,
/// while buckets are stored in UTC; converting through TimeZoneInfo keeps 23- and 25-hour DST
/// days right instead of assuming every day is 86,400 seconds.
/// </summary>
public static class LocalDays
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public static DateTimeOffset EndOf(DateOnly day) => StartOf(day.AddDays(1));

    public static DateOnly Of(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToLocalTime().DateTime);
}
