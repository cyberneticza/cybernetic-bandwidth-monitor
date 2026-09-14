using NetTrace.Core.Models;

namespace NetTrace.Core.Services;

/// <summary>One local day's traffic: in total, per application and, where hour detail survives, per local hour.</summary>
public sealed class DayUsage(DateOnly day)
{
    public DateOnly Day { get; } = day;
    public long Sent { get; private set; }
    public long Received { get; private set; }

    /// <summary>Application id to its traffic that day.</summary>
    public Dictionary<long, (long Sent, long Received)> Apps { get; } = [];

    /// <summary>Null when only the stored day total survives for this day.</summary>
    public long[]? HourSent { get; private set; }
    public long[]? HourReceived { get; private set; }

    public bool HasHours => HourSent is not null;

    public long Measure(Direction direction) => Pick(direction, Sent, Received);

    public long HourMeasure(int hour, Direction direction) =>
        HourSent is null ? 0 : Pick(direction, HourSent[hour], HourReceived![hour]);

    internal void Add(long appId, long sent, long received, int? hour)
    {
        Sent += sent;
        Received += received;
        Apps.TryGetValue(appId, out var sum);
        Apps[appId] = (sum.Sent + sent, sum.Received + received);

        if (hour is { } h)
        {
            HourSent ??= new long[24];
            HourReceived ??= new long[24];
            HourSent[h] += sent;
            HourReceived[h] += received;
        }
    }

    internal static long Pick(Direction direction, long sent, long received) => direction switch
    {
        Direction.Upload => sent,
        Direction.Download => received,
        _ => sent + received
    };
}

/// <summary>
/// A range of local days as loaded from the store. Days are local calendar days while buckets are
/// stored in UTC, so hour buckets (kept for <see cref="RetentionOptions.HourBucketDays"/>) are filed
/// under their local day and hour; older days only have UTC day buckets, filed under their UTC date.
/// </summary>
public sealed class UsageHistoryData
{
    public DateOnly First { get; }
    public DateOnly Last { get; }
    public IReadOnlyDictionary<long, string> AppNames { get; }
    public IReadOnlyList<DayUsage> Days { get; }
    public IReadOnlyList<NetworkEventRecord> Events { get; }

    private UsageHistoryData(DateOnly first, DateOnly last, Dictionary<long, string> names, List<DayUsage> days,
        IReadOnlyList<NetworkEventRecord> events)
    {
        First = first;
        Last = last;
        AppNames = names;
        Days = days;
        Events = events;
    }

    public int DayCount => Last.DayNumber - First.DayNumber + 1;

    public static UsageHistoryData Create(
        DateOnly first, DateOnly last,
        IReadOnlyList<UsageSeries> hourly, IReadOnlyList<UsageSeries> daily,
        IReadOnlyList<NetworkEventRecord> events)
    {
        var names = new Dictionary<long, string>();
        var byDay = new Dictionary<DateOnly, DayUsage>();

        DayUsage For(DateOnly day)
        {
            if (!byDay.TryGetValue(day, out var usage)) byDay[day] = usage = new DayUsage(day);
            return usage;
        }

        foreach (var app in hourly)
        {
            names[app.AppId] = app.DisplayName;
            foreach (var p in app.Points)
            {
                var local = p.BucketStartUtc.ToLocalTime();
                var day = DateOnly.FromDateTime(local.DateTime);
                if (day < first || day > last) continue;
                For(day).Add(app.AppId, p.BytesSent, p.BytesReceived, local.Hour);
            }
        }

        // Day buckets only fill days the hour detail did not reach.
        var hourDays = byDay.Keys.ToHashSet();
        foreach (var app in daily)
        {
            names.TryAdd(app.AppId, app.DisplayName);
            foreach (var p in app.Points)
            {
                var day = DateOnly.FromDateTime(p.BucketStartUtc.UtcDateTime);
                if (day < first || day > last || hourDays.Contains(day)) continue;
                For(day).Add(app.AppId, p.BytesSent, p.BytesReceived, hour: null);
            }
        }

        var days = new List<DayUsage>(last.DayNumber - first.DayNumber + 1);
        for (var day = first; day <= last; day = day.AddDays(1)) days.Add(byDay.TryGetValue(day, out var u) ? u : new DayUsage(day));

        return new UsageHistoryData(first, last, names, days, events);
    }
}

public sealed record HistoryApp(
    long AppId, string Name, long Sent, long Received, int DaysActive, DateOnly BusiestDay, long BusiestDayBytes,
    long? PreviousBytes)
{
    public long Measure(Direction direction) => DayUsage.Pick(direction, Sent, Received);
}

public sealed record HistoryDay(
    DateOnly Day, long Sent, long Received, long Measure, bool IsRecorded, bool IsUnusual,
    long? TopAppId, long TopAppBytes, int? BusiestHour, long BusiestHourBytes, int Problems, int Changes);

/// <summary>
/// What the History page reports for a range, in the measures traffic dashboards commonly use:
/// totals and change against the previous period, typical and 95th-percentile days, top talkers,
/// day-of-week and hour-of-day patterns, and days far outside the usual spread.
///
/// Averages cover the <em>recorded</em> span only, from the first day NetTrace saw traffic in the
/// range, so installing NetTrace halfway through a month does not halve its daily average.
/// </summary>
public sealed class UsageHistoryReport
{
    /// <summary>Fewer recorded days than this and "unusual" means nothing.</summary>
    public const int UnusualMinimumDays = 7;

    public Direction Direction { get; }
    public UsageHistoryData Data { get; }
    public UsageHistoryData? Previous { get; }

    public IReadOnlyList<HistoryDay> Days { get; }
    public IReadOnlyList<HistoryApp> Apps { get; }

    public long TotalSent { get; }
    public long TotalReceived { get; }
    public long Total { get; }

    /// <summary>First day with traffic in the range; null when nothing was recorded.</summary>
    public DateOnly? FirstRecorded { get; }
    public int RecordedDays { get; }
    public int DaysWithTraffic { get; }

    public double DailyAverage { get; }
    public double DailyMedian { get; }
    public double Daily95thPercentile { get; }
    public double DailyStandardDeviation { get; }
    public HistoryDay? BusiestDay { get; }
    public HistoryDay? QuietestDay { get; }

    /// <summary>Average per weekday, Monday first, over recorded days.</summary>
    public double[] WeekdayAverages { get; } = new double[7];
    public int[] WeekdayCounts { get; } = new int[7];

    /// <summary>Average per weekday (Monday first) and local hour, over recorded days that have hour detail.</summary>
    public double[,] HourAverages { get; } = new double[7, 24];
    public int HourDetailDays { get; }

    /// <summary>The three-hour window with the most traffic on an average day; null without hour detail.</summary>
    public int? PeakWindowStart { get; }

    public long? PreviousTotal { get; }
    public int PreviousRecordedDays { get; }
    public double? PreviousDailyAverage { get; }

    public UsageHistoryReport(UsageHistoryData data, UsageHistoryData? previous, Direction direction)
    {
        Data = data;
        Previous = previous;
        Direction = direction;

        var recorded = data.Days.SkipWhile(d => d.Sent + d.Received == 0).ToList();
        FirstRecorded = recorded.FirstOrDefault()?.Day;
        RecordedDays = recorded.Count;
        DaysWithTraffic = recorded.Count(d => d.Sent + d.Received > 0);

        TotalSent = data.Days.Sum(d => d.Sent);
        TotalReceived = data.Days.Sum(d => d.Received);
        Total = DayUsage.Pick(direction, TotalSent, TotalReceived);

        var measures = recorded.Select(d => (double)d.Measure(direction)).ToList();
        if (measures.Count > 0)
        {
            DailyAverage = measures.Average();
            DailyStandardDeviation = Math.Sqrt(measures.Average(m => (m - DailyAverage) * (m - DailyAverage)));
            var sorted = measures.Order().ToList();
            DailyMedian = Percentile(sorted, 0.5);
            Daily95thPercentile = Percentile(sorted, 0.95);
        }

        double unusualAbove = RecordedDays >= UnusualMinimumDays
            ? Math.Max(DailyAverage + 2 * DailyStandardDeviation, DailyAverage * 1.5)
            : double.MaxValue;

        var eventsByDay = data.Events.GroupBy(e => LocalDays.Of(e.ObservedUtc)).ToDictionary(g => g.Key, g => g.ToList());

        Days = data.Days.Select(d =>
        {
            long measure = d.Measure(direction);
            bool isRecorded = FirstRecorded is { } firstDay && d.Day >= firstDay;
            var top = d.Apps.Count == 0 ? default : d.Apps.MaxBy(a => DayUsage.Pick(direction, a.Value.Sent, a.Value.Received));
            int? busiestHour = null;
            long busiestHourBytes = 0;
            if (d.HasHours)
            {
                for (int h = 0; h < 24; h++)
                {
                    long v = d.HourMeasure(h, direction);
                    if (v > busiestHourBytes) (busiestHour, busiestHourBytes) = (h, v);
                }
            }
            eventsByDay.TryGetValue(d.Day, out var events);
            return new HistoryDay(
                d.Day, d.Sent, d.Received, measure, isRecorded,
                IsUnusual: isRecorded && measure > unusualAbove,
                TopAppId: d.Apps.Count == 0 ? null : top.Key,
                TopAppBytes: d.Apps.Count == 0 ? 0 : DayUsage.Pick(direction, top.Value.Sent, top.Value.Received),
                busiestHour, busiestHourBytes,
                Problems: events?.Count(e => e.IsProblem) ?? 0,
                Changes: events?.Count(e => !e.IsProblem) ?? 0);
        }).ToList();

        var withTraffic = Days.Where(d => d.Measure > 0).ToList();
        BusiestDay = withTraffic.MaxBy(d => d.Measure);
        // Today is not over yet, so it would nearly always look like the quietest day.
        QuietestDay = withTraffic.Where(d => d.Day != LocalDays.Today).MinBy(d => d.Measure);

        // Weekday and hour patterns.
        var hourSums = new double[7, 24];
        var hourCounts = new int[7];
        foreach (var d in recorded)
        {
            int wd = MondayIndex(d.Day.DayOfWeek);
            WeekdayAverages[wd] += d.Measure(direction);
            WeekdayCounts[wd]++;
            if (!d.HasHours) continue;
            hourCounts[wd]++;
            for (int h = 0; h < 24; h++) hourSums[wd, h] += d.HourMeasure(h, direction);
        }
        for (int wd = 0; wd < 7; wd++)
        {
            if (WeekdayCounts[wd] > 0) WeekdayAverages[wd] /= WeekdayCounts[wd];
            for (int h = 0; h < 24; h++) HourAverages[wd, h] = hourCounts[wd] == 0 ? 0 : hourSums[wd, h] / hourCounts[wd];
        }
        HourDetailDays = hourCounts.Sum();

        if (HourDetailDays > 0)
        {
            var byHour = Enumerable.Range(0, 24)
                .Select(h => Enumerable.Range(0, 7).Sum(wd => HourAverages[wd, h] * hourCounts[wd]) / HourDetailDays)
                .ToArray();
            double best = -1;
            for (int start = 0; start <= 21; start++)
            {
                double window = byHour[start] + byHour[start + 1] + byHour[start + 2];
                if (window > best) (best, PeakWindowStart) = (window, start);
            }
            if (best <= 0) PeakWindowStart = null;
        }

        // Applications, busiest first.
        var previousByApp = previous?.Days
            .SelectMany(d => d.Apps)
            .GroupBy(a => a.Key)
            .ToDictionary(g => g.Key, g => g.Sum(a => DayUsage.Pick(direction, a.Value.Sent, a.Value.Received)));

        Apps = data.Days
            .SelectMany(d => d.Apps.Select(a => (d.Day, AppId: a.Key, a.Value.Sent, a.Value.Received)))
            .GroupBy(x => x.AppId)
            .Select(g =>
            {
                var busiest = g.MaxBy(x => DayUsage.Pick(direction, x.Sent, x.Received));
                long? before = previousByApp is null ? null : previousByApp.GetValueOrDefault(g.Key);
                return new HistoryApp(
                    g.Key,
                    data.AppNames.GetValueOrDefault(g.Key, "Unknown application"),
                    g.Sum(x => x.Sent), g.Sum(x => x.Received),
                    g.Count(x => x.Sent + x.Received > 0),
                    busiest.Day, DayUsage.Pick(direction, busiest.Sent, busiest.Received),
                    before);
            })
            .Where(a => a.Measure(direction) > 0)
            .OrderByDescending(a => a.Measure(direction))
            .ToList();

        if (previous is not null)
        {
            var previousRecorded = previous.Days.SkipWhile(d => d.Sent + d.Received == 0).ToList();
            PreviousRecordedDays = previousRecorded.Count;
            PreviousTotal = previous.Days.Sum(d => d.Measure(direction));
            PreviousDailyAverage = previousRecorded.Count == 0 ? null : previousRecorded.Average(d => (double)d.Measure(direction));
        }
    }

    /// <summary>Monday is 0: the ISO week, which is how most of the world reads a week.</summary>
    public static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double rank = p * (sorted.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}
