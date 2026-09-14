using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;
using SkiaSharp;

namespace CyberneticBandwidth.App.ViewModels;

public enum HistoryGrouping
{
    Auto,
    Day,
    Week,
    Month
}

public sealed record GroupingOption(HistoryGrouping Value, string Label);

public sealed record RangePreset(string Label, Func<DateOnly, (DateOnly First, DateOnly Last)> Resolve);

/// <summary>One figure at the top of the page; Change compares it with the previous period.</summary>
public sealed record SummaryCard(string Title, string Value, string Detail, string? Change);

public enum HighlightTone
{
    Neutral,
    Good,
    Warning
}

public sealed record Highlight(string Glyph, string Text, HighlightTone Tone)
{
    public bool IsWarning => Tone == HighlightTone.Warning;
    public bool IsGood => Tone == HighlightTone.Good;
}

/// <summary>A heatmap square. Opacity is the share of the busiest square; blank squares are outside the range.</summary>
public sealed record HeatCell(double Opacity, bool IsBlank, string? Tooltip, DateOnly? Day);

public sealed record HeatWeek(string MonthLabel, IReadOnlyList<HeatCell> Cells);

public sealed record HeatRow(string Label, IReadOnlyList<HeatCell> Cells);

public sealed record HistoryAppRow(
    Brush Swatch, string Name, long Downloaded, long Uploaded, long Total, double Share, string ShareText,
    int DaysActive, string BusiestDay, string Change, int ChangeDirection, double ChangeSort)
{
    public bool IsUp => ChangeDirection > 0;
    public bool IsDown => ChangeDirection < 0;
}

public sealed record HistoryDayRow(
    DateOnly Day, string DayLabel, long Downloaded, long Uploaded, long Total, string VsAverage, double VsAverageSort,
    string TopApp, string BusiestHour, string Network, bool HasProblems, string Note, bool IsHighlighted, bool IsRecorded);

/// <summary>A day in the range picker's month grid.</summary>
public sealed record PickerDay(DateOnly Date, string Label, bool IsInMonth, bool IsEnabled, bool IsInRange, bool IsEdge,
    bool IsToday, bool HasData);

/// <summary>
/// History: any range of days, reported the way traffic dashboards and usage meters report it.
/// Totals and daily figures with change against the period before; a plain-language list of what
/// stands out; daily usage with its average, trend and unusual days; which days of the week and
/// hours of the day are busiest; the top applications over the range; and a log of every day.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private const int TopAppsCharted = 6;
    private const int PickerCellCount = 42;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ThemeService _theme;
    private readonly RetentionOptions _retention;
    private readonly UsageAlertService _alerts;
    private readonly GraphsViewModel _trends;
    private readonly NavigationRequests _navigation;
    private readonly ILogger<HistoryViewModel> _log;

    private UsageHistoryData? _current;
    private UsageHistoryData? _previous;
    private UsageHistoryReport? _report;
    private HashSet<DateOnly> _daysWithData = [];
    private DateOnly? _firstDataDay;
    private int _request;

    // Charts index days and buckets by position; these are what the axis labellers and clicks read.
    private List<(DateOnly Start, string Label)> _buckets = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeLabel), nameof(RangeDays), nameof(PreviousRangeLabel))]
    private DateOnly _rangeFirst = LocalDays.Today.AddDays(-29);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeLabel), nameof(RangeDays), nameof(PreviousRangeLabel))]
    private DateOnly _rangeLast = LocalDays.Today;

    [ObservableProperty] private Direction _direction = Direction.Both;
    [ObservableProperty] private GroupingOption _selectedGrouping;
    [ObservableProperty] private bool _compareWithPrevious = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _coverageNote;

    [ObservableProperty] private ISeries[] _dailySeries = [];
    [ObservableProperty] private Axis[] _dailyXAxes = [];
    [ObservableProperty] private Axis[] _dailyYAxes = [];
    [ObservableProperty] private RectangularSection[] _dailySections = [];
    [ObservableProperty] private string _dailyTitle = "Daily usage";
    [ObservableProperty] private bool _showTrendLegend;
    [ObservableProperty] private bool _showPreviousLegend;
    [ObservableProperty] private bool _showUnusualLegend;
    [ObservableProperty] private bool _barsAreDays = true;

    [ObservableProperty] private ISeries[] _weekdaySeries = [];
    [ObservableProperty] private Axis[] _weekdayXAxes = [];
    [ObservableProperty] private Axis[] _weekdayYAxes = [];
    [ObservableProperty] private string? _weekdaySummary;

    [ObservableProperty] private string? _calendarSummary;
    [ObservableProperty] private string? _hourSummary;
    [ObservableProperty] private string? _hourNote;

    [ObservableProperty] private ISeries[] _appSeries = [];
    [ObservableProperty] private Axis[] _appXAxes = [];
    [ObservableProperty] private Axis[] _appYAxes = [];
    [ObservableProperty] private string? _appsSummary;

    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = null!;
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = null!;

    // Range picker
    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private DateOnly _pickerMonth;
    [ObservableProperty] private string _pickerMonthLabel = string.Empty;
    [ObservableProperty] private string _pickerHint = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomRangeCommand))]
    private DateOnly? _pendingFirst;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomRangeCommand))]
    private DateOnly? _pendingLast;

    public ObservableCollection<SummaryCard> Cards { get; } = [];
    public ObservableCollection<Highlight> Highlights { get; } = [];
    public ObservableCollection<HeatWeek> CalendarWeeks { get; } = [];
    public ObservableCollection<HeatRow> HourRows { get; } = [];
    public ObservableCollection<HistoryAppRow> AppRows { get; } = [];
    public ObservableCollection<HistoryDayRow> DayRows { get; } = [];
    public ObservableCollection<PickerDay> PickerDays { get; } = [];

    public IReadOnlyList<string> HourLabels { get; } =
        Enumerable.Range(0, 24).Select(h => h % 3 == 0 ? $"{h:00}" : string.Empty).ToList();

    public IReadOnlyList<string> WeekdayHeaders { get; } =
        Enumerable.Range(0, 7).Select(i => WeekdayName(i, abbreviated: true)).Select(n => n.Length > 2 ? n[..2] : n).ToList();

    public IReadOnlyList<DirectionOption> Directions { get; } =
    [
        new(Direction.Both, "Up and down"),
        new(Direction.Download, "Downloaded"),
        new(Direction.Upload, "Uploaded")
    ];

    public IReadOnlyList<GroupingOption> Groupings { get; } =
    [
        new(HistoryGrouping.Auto, "Auto"),
        new(HistoryGrouping.Day, "Day"),
        new(HistoryGrouping.Week, "Week"),
        new(HistoryGrouping.Month, "Month")
    ];

    public IReadOnlyList<RangePreset> Presets { get; }

    public int RangeDays => RangeLast.DayNumber - RangeFirst.DayNumber + 1;

    public string RangeLabel => $"{FormatRange(RangeFirst, RangeLast)} · {Plural(RangeDays, "day")}";

    public string PreviousRangeLabel => FormatRange(RangeFirst.AddDays(-RangeDays), RangeFirst.AddDays(-1));

    /// <summary>The oldest day any usage can still be stored for.</summary>
    private DateOnly MinDay => LocalDays.Today.AddDays(-(_retention.DayBucketDays - 1));

    public HistoryViewModel(
        IServiceScopeFactory scopes,
        ThemeService theme,
        IOptions<RetentionOptions> retention,
        UsageAlertService alerts,
        GraphsViewModel trends,
        NavigationRequests navigation,
        ILogger<HistoryViewModel> log)
    {
        _scopes = scopes;
        _theme = theme;
        _retention = retention.Value;
        _alerts = alerts;
        _trends = trends;
        _navigation = navigation;
        _log = log;
        _selectedGrouping = Groupings[0];

        Presets =
        [
            new("Last 7 days", t => (t.AddDays(-6), t)),
            new("Last 30 days", t => (t.AddDays(-29), t)),
            new("Last 90 days", t => (t.AddDays(-89), t)),
            new("This month", t => (new DateOnly(t.Year, t.Month, 1), t)),
            new("Last month", t =>
            {
                var first = new DateOnly(t.Year, t.Month, 1).AddMonths(-1);
                return (first, first.AddMonths(1).AddDays(-1));
            }),
            new("This year", t => (new DateOnly(t.Year, 1, 1), t)),
            new("Last 12 months", t => (t.AddMonths(-12).AddDays(1), t)),
            new("Everything recorded", t => (_firstDataDay ?? t.AddDays(-29), t))
        ];

        ApplyChrome();
        theme.ThemeChanged += () =>
        {
            ApplyChrome();
            Rebuild();
        };
    }

    partial void OnDirectionChanged(Direction value) => Rebuild();

    partial void OnSelectedGroupingChanged(GroupingOption value) => Rebuild();

    partial void OnCompareWithPreviousChanged(bool value) => Rebuild();

    // ================================================================ loading

    [RelayCommand]
    public async Task ReloadAsync()
    {
        int request = ++_request;
        IsBusy = true;

        try
        {
            // Open across midnight, or a preset from an earlier day: never reach into the future.
            var today = LocalDays.Today;
            if (RangeLast > today) RangeLast = today;
            if (RangeFirst > RangeLast) RangeFirst = RangeLast;

            var first = RangeFirst;
            var last = RangeLast;
            int days = RangeDays;

            await using var scope = _scopes.CreateAsyncScope();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
            var network = scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>();

            // Which days hold anything at all: dots in the range picker and "Everything recorded".
            // Day bins are UTC days, close enough for both.
            var all = await usage.GetTimelineAsync(LocalDays.StartOf(MinDay), LocalDays.EndOf(today), Day, CancellationToken.None);
            var withData = all.Where(p => p.BytesSent + p.BytesReceived > 0)
                .Select(p => DateOnly.FromDateTime(p.BucketStartUtc.UtcDateTime))
                .ToHashSet();

            var current = await LoadAsync(usage, network, first, last);
            var previous = await LoadAsync(usage, null, first.AddDays(-days), first.AddDays(-1));

            if (request != _request) return;

            _daysWithData = withData;
            _firstDataDay = withData.Count == 0 ? null : withData.Min();
            _current = current;
            _previous = previous;
            Rebuild();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load usage history.");
            if (request == _request)
            {
                HasData = false;
                Message = "The usage history could not be read. The log file has the details.";
            }
        }
        finally
        {
            if (request == _request) IsBusy = false;
        }
    }

    private async Task<UsageHistoryData> LoadAsync(
        IUsageRepository usage, INetworkHistoryRepository? network, DateOnly first, DateOnly last)
    {
        var from = LocalDays.StartOf(first);
        var to = LocalDays.EndOf(last);

        // Hours survive HourBucketDays and are filed under local days and hours; older days only
        // have UTC day buckets.
        var hourlyFrom = LocalDays.StartOf(LocalDays.Today.AddDays(-(_retention.HourBucketDays - 1)));
        if (hourlyFrom < from) hourlyFrom = from;

        IReadOnlyList<UsageSeries> hourly = hourlyFrom < to
            ? await usage.GetAppTimelineAsync(hourlyFrom, to, Hour, CancellationToken.None)
            : [];
        IReadOnlyList<UsageSeries> daily = from < hourlyFrom
            ? await usage.GetAppTimelineAsync(from, hourlyFrom, Day, CancellationToken.None)
            : [];
        IReadOnlyList<NetworkEventRecord> events = network is null
            ? []
            : await network.GetNetworkEventsAsync(from, to, CancellationToken.None);

        return await Task.Run(() => UsageHistoryData.Create(first, last, hourly, daily, events));
    }

    // ================================================================ building

    private void Rebuild()
    {
        if (_current is null) return;

        // A previous period with nothing recorded is not a comparison: every application would read "new".
        bool previousRecorded = _previous?.Days.Any(d => d.Sent + d.Received > 0) == true;
        var report = new UsageHistoryReport(_current, CompareWithPrevious && previousRecorded ? _previous : null, Direction);
        _report = report;

        HasData = report.FirstRecorded is not null;
        Message = HasData ? null : "No usage was recorded in this range. Pick another range, or leave Cybernetic Bandwidth Monitor running and check back.";

        var notes = new List<string>();
        if (HasData && report.RecordedDays < _current.DayCount)
            notes.Add($"Cybernetic Bandwidth Monitor's records in this range start on {report.FirstRecorded:dddd d MMMM}, so averages and patterns use " +
                      $"those {Plural(report.RecordedDays, "day")} rather than all {_current.DayCount}.");
        if (HasData && CompareWithPrevious && !previousRecorded)
            notes.Add($"Nothing was recorded in the {Plural(RangeDays, "day")} before, so there is no comparison.");
        CoverageNote = notes.Count == 0 ? null : string.Join(" ", notes);

        BuildCards(report);
        BuildHighlights(report);
        BuildDailyChart(report);
        BuildWeekdays(report);
        BuildCalendar(report);
        BuildHours(report);
        BuildApps(report);
        BuildDayRows(report);
    }

    private string Versus => $"previous {Plural(RangeDays, "day")}";

    private void BuildCards(UsageHistoryReport r)
    {
        Cards.Clear();
        if (!HasData) return;

        string totalDetail = $"↓ {ByteFormat.Size(r.TotalReceived)}   ↑ {ByteFormat.Size(r.TotalSent)}";
        Cards.Add(new SummaryCard(TotalTitle, ByteFormat.Size(r.Total), totalDetail, PercentChange(r.Total, r.PreviousTotal)));

        Cards.Add(new SummaryCard(
            "Daily average",
            ByteFormat.Size(r.DailyAverage),
            $"Typical day {ByteFormat.Size(r.DailyMedian)} · 95th percentile {ByteFormat.Size(r.Daily95thPercentile)}",
            PercentChange(r.DailyAverage, r.PreviousDailyAverage)));

        if (r.BusiestDay is { } busiest)
        {
            string times = r.DailyAverage > 0 ? $" · {busiest.Measure / r.DailyAverage:0.#}× the average" : string.Empty;
            Cards.Add(new SummaryCard("Busiest day", ByteFormat.Size(busiest.Measure), $"{busiest.Day:ddd d MMM}{times}", null));
        }

        int busiestWeekday = Enumerable.Range(0, 7).MaxBy(i => r.WeekdayAverages[i]);
        Cards.Add(new SummaryCard(
            "Busiest time",
            r.PeakWindowStart is { } start ? $"{start:00}:00–{start + 3:00}:00" : "—",
            r.WeekdayAverages[busiestWeekday] > 0 ? $"Busiest weekday: {WeekdayName(busiestWeekday, false)}" : "Not enough detail",
            null));

        if (r.Apps.Count > 0 && r.Total > 0)
        {
            var top = r.Apps[0];
            long measure = top.Measure(Direction);
            Cards.Add(new SummaryCard("Top application", top.Name,
                $"{Percent(measure, r.Total)} of traffic · {ByteFormat.Size(measure)}", null));
        }
    }

    private string TotalTitle => Direction switch
    {
        Direction.Download => "Downloaded",
        Direction.Upload => "Uploaded",
        _ => "Total used"
    };

    /// <summary>Plain-language findings, most telling first. Each appears only when it says something.</summary>
    private void BuildHighlights(UsageHistoryReport r)
    {
        Highlights.Clear();
        if (!HasData) return;

        string measured = Direction switch { Direction.Download => "downloaded", Direction.Upload => "uploaded", _ => "used" };

        // Against the period before.
        if (r.PreviousTotal is { } before && before > 0)
        {
            double change = (r.Total - (double)before) / before;
            string how = Math.Abs(change) < 0.03 ? "about the same as"
                : change > 0 ? $"{change * 100:0}% more than" : $"{-change * 100:0}% less than";
            Highlights.Add(new Highlight("\uE9D2",
                $"You {measured} {ByteFormat.Size(r.Total)} in these {Plural(RangeDays, "day")}, {how} the {Plural(RangeDays, "day")} before ({ByteFormat.Size(before)}).",
                HighlightTone.Neutral));
        }
        else
        {
            Highlights.Add(new Highlight("\uE9D2",
                $"You {measured} {ByteFormat.Size(r.Total)} in these {Plural(RangeDays, "day")}, about {ByteFormat.Size(r.DailyAverage)} on a recorded day.",
                HighlightTone.Neutral));
        }

        // The busiest day and what drove it.
        if (r.BusiestDay is { } busiest && r.RecordedDays > 1)
        {
            string text = $"{busiest.Day:dddd d MMMM} was the busiest day at {ByteFormat.Size(busiest.Measure)}";
            if (r.DailyAverage > 0) text += $", {busiest.Measure / r.DailyAverage:0.#}× the daily average";
            if (busiest.TopAppId is { } appId && busiest.Measure > 0)
                text += $". {AppName(appId)} accounted for {Percent(busiest.TopAppBytes, busiest.Measure)} of it";
            Highlights.Add(new Highlight("\uE787", text + ".", HighlightTone.Neutral));
        }

        // Days far outside the usual spread.
        var unusual = r.Days.Where(d => d.IsUnusual).OrderByDescending(d => d.Measure).ToList();
        if (unusual.Count > 0)
        {
            string list = string.Join(", ", unusual.Take(3).Select(d => $"{d.Day:ddd d MMM} ({ByteFormat.Size(d.Measure)})"));
            if (unusual.Count > 3) list += $" and {unusual.Count - 3} more";
            Highlights.Add(new Highlight("\uE7BA",
                $"{Plural(unusual.Count, "unusually heavy day")}, well above your usual spread: {list}.",
                HighlightTone.Warning));
        }

        // Weekdays against weekends.
        int weekdays = r.WeekdayCounts.Take(5).Sum();
        int weekends = r.WeekdayCounts.Skip(5).Sum();
        if (weekdays > 0 && weekends > 0 && r.RecordedDays >= 7)
        {
            double weekdayAverage = Enumerable.Range(0, 5).Sum(i => r.WeekdayAverages[i] * r.WeekdayCounts[i]) / weekdays;
            double weekendAverage = Enumerable.Range(5, 2).Sum(i => r.WeekdayAverages[i] * r.WeekdayCounts[i]) / weekends;
            string text = weekendAverage <= 0 ? $"All the traffic was on weekdays, {ByteFormat.Size(weekdayAverage)} a day on average; weekends had none."
                : weekdayAverage <= 0 ? $"All the traffic was at weekends, {ByteFormat.Size(weekendAverage)} a day on average; weekdays had none."
                : $"Weekdays average {ByteFormat.Size(weekdayAverage)} and weekend days {ByteFormat.Size(weekendAverage)}" +
                  (weekendAverage > weekdayAverage * 1.25 ? $", so weekends are {weekendAverage / weekdayAverage:0.#}× busier."
                   : weekdayAverage > weekendAverage * 1.25 ? $", so weekdays are {weekdayAverage / weekendAverage:0.#}× busier."
                   : ", much the same.");
            Highlights.Add(new Highlight("\uE8BF",
                text, HighlightTone.Neutral));
        }

        // When in the day.
        if (r.PeakWindowStart is { } start)
        {
            double day = 0, window = 0;
            for (int h = 0; h < 24; h++)
            {
                double hour = Enumerable.Range(0, 7).Sum(wd => r.HourAverages[wd, h]);
                day += hour;
                if (h >= start && h < start + 3) window += hour;
            }
            if (day > 0)
                Highlights.Add(new Highlight("\uE823",
                    $"Most traffic happens between {start:00}:00 and {start + 3:00}:00, {window / day * 100:0}% of an average day in three hours.",
                    HighlightTone.Neutral));
        }

        // Upload share. Browsing and streaming download far more than they send.
        long both = r.TotalSent + r.TotalReceived;
        if (both > 0 && r.TotalSent >= both * 0.35)
        {
            Highlights.Add(new Highlight("\uE898",
                $"Uploads are {Percent(r.TotalSent, both)} of all traffic. Everyday browsing and streaming download far more than they upload, " +
                "so a share this high usually means cloud backup or file sync, lots of video calls, or a security proxy relaying traffic.",
                HighlightTone.Warning));
        }

        // Applications that moved against the previous period.
        if (r.Previous is not null)
        {
            var grew = r.Apps
                .Where(a => a.PreviousBytes is > 0)
                .Select(a => (App: a, Now: a.Measure(Direction), Before: a.PreviousBytes!.Value))
                .Where(x => x.Now - x.Before >= 50 * ByteFormat.Megabyte && x.Now >= x.Before * 1.5)
                .OrderByDescending(x => x.Now - x.Before)
                .FirstOrDefault();
            if (grew.App is not null)
                Highlights.Add(new Highlight("\uE71D",
                    $"{grew.App.Name} grew the most: {ByteFormat.Size(grew.Before)} in the previous period, {ByteFormat.Size(grew.Now)} in this one.",
                    HighlightTone.Neutral));

            var newcomer = r.Apps
                .Where(a => a.PreviousBytes is 0 && a.Measure(Direction) >= 100 * ByteFormat.Megabyte)
                .FirstOrDefault();
            if (newcomer is not null)
                Highlights.Add(new Highlight("\uE71D",
                    $"New in this period: {newcomer.Name}, {ByteFormat.Size(newcomer.Measure(Direction))}, with nothing in the {Plural(RangeDays, "day")} before.",
                    HighlightTone.Neutral));
        }

        // Connection problems.
        var problemDays = r.Days.Where(d => d.Problems > 0).ToList();
        if (problemDays.Count > 0)
        {
            var worst = problemDays.MaxBy(d => d.Problems)!;
            Highlights.Add(new Highlight("\uE7BA",
                $"{Plural(problemDays.Sum(d => d.Problems), "network problem")} on {Plural(problemDays.Count, "day")}; the most on {worst.Day:ddd d MMM} ({worst.Problems}). Drops and slow spells are in the day log below.",
                HighlightTone.Warning));
        }
        else if (r.RecordedDays >= 3)
        {
            Highlights.Add(new Highlight("\uE73E", "No drops, outages or slow spells were recorded in this range.", HighlightTone.Good));
        }

        // The monthly budget, the way usage meters show it: used so far and where the pace lands.
        if (_alerts.Latest is { MonthlyBudgetBytes: > 0 } budget)
        {
            var today = LocalDays.Today;
            var cycleEnd = budget.PeriodStart.AddMonths(1);
            int elapsed = today.DayNumber - budget.PeriodStart.DayNumber + 1;
            int length = cycleEnd.DayNumber - budget.PeriodStart.DayNumber;
            double projected = elapsed <= 0 ? budget.PeriodBytes : budget.PeriodBytes / (double)elapsed * length;
            bool over = projected > budget.MonthlyBudgetBytes;
            Highlights.Add(new Highlight("\uE8C7",
                $"Monthly budget: {ByteFormat.Size(budget.PeriodBytes)} of {ByteFormat.Size(budget.MonthlyBudgetBytes.Value)} used since {budget.PeriodStart:d MMM}. " +
                $"At this pace the cycle ends near {ByteFormat.Size(projected)} on {cycleEnd.AddDays(-1):d MMM}{(over ? ", over the budget" : ", within the budget")}.",
                over ? HighlightTone.Warning : HighlightTone.Good));
        }
    }

    // ---------------------------------------------------------------- daily chart

    private HistoryGrouping EffectiveGrouping(UsageHistoryReport r) => SelectedGrouping.Value switch
    {
        HistoryGrouping.Auto when r.Data.DayCount <= 92 => HistoryGrouping.Day,
        HistoryGrouping.Auto when r.Data.DayCount <= 400 => HistoryGrouping.Week,
        HistoryGrouping.Auto => HistoryGrouping.Month,
        var g => g
    };

    private static DateOnly BucketOf(DateOnly day, HistoryGrouping grouping) => grouping switch
    {
        HistoryGrouping.Week => day.AddDays(-UsageHistoryReport.MondayIndex(day.DayOfWeek)),
        HistoryGrouping.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day
    };

    private void BuildDailyChart(UsageHistoryReport r)
    {
        var theme = _theme.Chart;
        var grouping = EffectiveGrouping(r);
        BarsAreDays = grouping == HistoryGrouping.Day;
        string unit = grouping switch { HistoryGrouping.Week => "week", HistoryGrouping.Month => "month", _ => "day" };
        DailyTitle = grouping switch
        {
            HistoryGrouping.Week => "Weekly usage",
            HistoryGrouping.Month => "Monthly usage",
            _ => "Daily usage"
        };

        // Buckets in order, and each day's bucket index.
        _buckets = r.Days
            .Select(d => BucketOf(d.Day, grouping))
            .Distinct()
            .Select(start => (start, grouping switch
            {
                HistoryGrouping.Month => start.ToString("MMM yyyy"),
                HistoryGrouping.Week => $"w/c {start:d MMM}",
                _ => start.ToString("d MMM")
            }))
            .ToList();
        var index = _buckets.Select((b, i) => (b.Start, i)).ToDictionary(x => x.Start, x => x.i);

        int count = _buckets.Count;
        var sent = new double[count];
        var received = new double[count];
        var measure = new double[count];
        var previous = new double[count];
        var unusual = new bool[count];
        int firstRecordedBucket = r.FirstRecorded is { } fr ? index[BucketOf(fr, grouping)] : count;

        for (int i = 0; i < r.Days.Count; i++)
        {
            var d = r.Days[i];
            int b = index[BucketOf(d.Day, grouping)];
            sent[b] += d.Sent;
            received[b] += d.Received;
            measure[b] += d.Measure;
            unusual[b] |= d.IsUnusual;
            if (r.Previous is { } p && i < p.Days.Count) previous[b] += p.Days[i].Measure(Direction);
        }

        string Tip(ChartPoint p) => ByteFormat.Size(p.Coordinate.PrimaryValue);

        ObservablePoint[] Points(double[] values, int from = 0) =>
            values.Select((v, i) => new ObservablePoint(i, i < from ? null : v)).ToArray();

        var series = new List<ISeries>();

        if (Direction == Direction.Both)
        {
            var down = new StackedColumnSeries<ObservablePoint>
            {
                Name = "Downloaded", Values = Points(received), Fill = ChartPaints.Fill(theme.Download), Stroke = null,
                Padding = 1, MaxBarWidth = 40, Rx = 2, Ry = 2, YToolTipLabelFormatter = Tip
            };
            var up = new StackedColumnSeries<ObservablePoint>
            {
                Name = "Uploaded", Values = Points(sent), Fill = ChartPaints.Fill(theme.Upload), Stroke = null,
                Padding = 1, MaxBarWidth = 40, Rx = 2, Ry = 2, YToolTipLabelFormatter = Tip
            };
            down.DataPointerDown += (_, points) => OnBucketClicked(points.FirstOrDefault()?.Model, grouping);
            up.DataPointerDown += (_, points) => OnBucketClicked(points.FirstOrDefault()?.Model, grouping);
            series.Add(down);
            series.Add(up);
        }
        else
        {
            var column = new ColumnSeries<ObservablePoint>
            {
                Name = Direction == Direction.Download ? "Downloaded" : "Uploaded",
                Values = Points(measure),
                Fill = ChartPaints.Fill(Direction == Direction.Download ? theme.Download : theme.Upload), Stroke = null,
                Padding = 1, MaxBarWidth = 40, Rx = 2, Ry = 2, YToolTipLabelFormatter = Tip
            };
            column.DataPointerDown += (_, points) => OnBucketClicked(points.FirstOrDefault()?.Model, grouping);
            series.Add(column);
        }

        // A 7-day rolling average smooths weekly rhythm out of daily bars.
        ShowTrendLegend = grouping == HistoryGrouping.Day && r.RecordedDays >= 14;
        if (ShowTrendLegend)
        {
            var rolling = new double[count];
            for (int i = 0; i < count; i++)
            {
                int from = Math.Max(firstRecordedBucket, i - 6);
                rolling[i] = i < firstRecordedBucket ? 0 : Enumerable.Range(from, i - from + 1).Average(j => measure[j]);
            }
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = "7-day average", Values = Points(rolling, from: Math.Min(count, firstRecordedBucket + 6)),
                GeometrySize = 0, LineSmoothness = 0.4, Fill = null, Stroke = ChartPaints.Stroke(theme.Text, 2),
                YToolTipLabelFormatter = Tip
            });
        }

        ShowPreviousLegend = r.Previous is not null && previous.Any(v => v > 0);
        if (ShowPreviousLegend)
        {
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = "Previous period", Values = Points(previous),
                GeometrySize = 0, LineSmoothness = 0, Fill = null,
                Stroke = new SolidColorPaint(theme.Muted) { StrokeThickness = 1.5f, PathEffect = new DashEffect([5, 4]) },
                YToolTipLabelFormatter = Tip
            });
        }

        DailySeries = [.. series];

        var sections = new List<RectangularSection>();

        ShowUnusualLegend = false;
        for (int i = 0; i < count; i++)
        {
            if (!unusual[i]) continue;
            ShowUnusualLegend = true;
            sections.Add(new RectangularSection { Xi = i - 0.5, Xj = i + 0.5, Fill = ChartPaints.Fill(theme.Alert, 40) });
        }

        int recordedBuckets = count - firstRecordedBucket;
        if (recordedBuckets > 0)
        {
            double average = measure.Skip(firstRecordedBucket).Average();
            sections.Add(new RectangularSection
            {
                Yi = average, Yj = average,
                Stroke = new SolidColorPaint(theme.Muted) { StrokeThickness = 1, PathEffect = new DashEffect([2, 3]) },
                Label = $"average {ByteFormat.Size(average)} a {unit}",
                LabelPaint = ChartPaints.Fill(theme.Muted),
                LabelSize = 11
            });
        }
        DailySections = [.. sections];

        DailyXAxes =
        [
            new Axis
            {
                Labeler = x => BucketLabel(x),
                MinStep = 1,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
        DailyYAxes = [BytesAxis(theme)];
    }

    private string BucketLabel(double x)
    {
        int i = (int)Math.Round(x);
        return i >= 0 && i < _buckets.Count ? _buckets[i].Label : string.Empty;
    }

    private void OnBucketClicked(ObservablePoint? point, HistoryGrouping grouping)
    {
        if (grouping != HistoryGrouping.Day || point?.X is not { } x) return;
        int i = (int)Math.Round(x);
        if (i >= 0 && i < _buckets.Count) OpenDay(_buckets[i].Start);
    }

    // ---------------------------------------------------------------- patterns

    private void BuildWeekdays(UsageHistoryReport r)
    {
        var theme = _theme.Chart;
        var values = Enumerable.Range(0, 7).Select(i => new ObservablePoint(i, r.WeekdayAverages[i])).ToArray();

        WeekdaySeries =
        [
            new ColumnSeries<ObservablePoint>
            {
                Name = "Average day", Values = values,
                Fill = ChartPaints.Fill(Direction == Direction.Upload ? theme.Upload : theme.Download), Stroke = null,
                Padding = 6, MaxBarWidth = 44, Rx = 3, Ry = 3,
                YToolTipLabelFormatter = p =>
                {
                    int i = (int)Math.Round(p.Coordinate.SecondaryValue);
                    return i is >= 0 and < 7
                        ? $"{ByteFormat.Size(p.Coordinate.PrimaryValue)} (over {Plural(r.WeekdayCounts[i], "day")})"
                        : ByteFormat.Size(p.Coordinate.PrimaryValue);
                }
            }
        ];
        WeekdayXAxes =
        [
            new Axis
            {
                Labeler = x => (int)Math.Round(x) is var i and >= 0 and < 7 ? WeekdayName(i, true) : string.Empty,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
        WeekdayYAxes = [BytesAxis(theme)];

        var counted = Enumerable.Range(0, 7).Where(i => r.WeekdayCounts[i] > 0).ToList();
        if (counted.Count < 2)
        {
            WeekdaySummary = null;
            return;
        }
        int busiest = counted.MaxBy(i => r.WeekdayAverages[i]);
        int quietest = counted.MinBy(i => r.WeekdayAverages[i]);
        var idle = counted.Where(i => r.WeekdayAverages[i] <= 0).Select(i => $"{WeekdayName(i, false)}s").ToList();
        WeekdaySummary = $"Busiest on {WeekdayName(busiest, false)}s ({ByteFormat.Size(r.WeekdayAverages[busiest])} on average), " +
                         (idle.Count > 0
                             ? $"with no traffic on {string.Join(" or ", idle)}."
                             : $"quietest on {WeekdayName(quietest, false)}s ({ByteFormat.Size(r.WeekdayAverages[quietest])}).");
    }

    /// <summary>
    /// A calendar heatmap, one column per week: squares darken in four steps by quartile of the
    /// days with traffic, so one enormous day does not wash every other day out to nothing.
    /// </summary>
    private void BuildCalendar(UsageHistoryReport r)
    {
        CalendarWeeks.Clear();
        if (!HasData) return;

        var active = r.Days.Where(d => d.Measure > 0).Select(d => (double)d.Measure).Order().ToList();
        double Quantile(double q) => active.Count == 0 ? 0 : active[(int)Math.Floor(q * (active.Count - 1))];
        double q1 = Quantile(0.25), q2 = Quantile(0.5), q3 = Quantile(0.75);

        var byDay = r.Days.ToDictionary(d => d.Day);
        var start = BucketOf(r.Data.First, HistoryGrouping.Week);
        var end = r.Data.Last;
        string? lastMonth = null;

        for (var weekStart = start; weekStart <= end; weekStart = weekStart.AddDays(7))
        {
            var cells = new List<HeatCell>(7);
            string monthLabel = string.Empty;

            for (int i = 0; i < 7; i++)
            {
                var day = weekStart.AddDays(i);
                if (!byDay.TryGetValue(day, out var d))
                {
                    cells.Add(new HeatCell(0, IsBlank: true, null, null));
                    continue;
                }

                string month = day.ToString("MMM");
                if (month != lastMonth && (day.Day <= 7 || lastMonth is null))
                {
                    monthLabel = month;
                    lastMonth = month;
                }

                double opacity = d.Measure <= 0 ? 0
                    : d.Measure <= q1 ? 0.28
                    : d.Measure <= q2 ? 0.5
                    : d.Measure <= q3 ? 0.75
                    : 1;
                string tip = !d.IsRecorded
                    ? $"{day:dddd d MMMM yyyy}: not recorded"
                    : $"{day:dddd d MMMM yyyy}: {ByteFormat.Size(d.Measure)}" +
                      (d.TopAppId is { } app ? $"\nMost used: {AppName(app)} ({ByteFormat.Size(d.TopAppBytes)})" : string.Empty) +
                      (d.Problems > 0 ? $"\n{Plural(d.Problems, "network problem")}" : string.Empty) +
                      "\nClick to open this day in Trends";
                cells.Add(new HeatCell(opacity, IsBlank: false, tip, day));
            }

            CalendarWeeks.Add(new HeatWeek(monthLabel, cells));
        }

        CalendarSummary = r.DaysWithTraffic == r.RecordedDays
            ? $"Traffic on all {Plural(r.RecordedDays, "recorded day")}. Darker squares are busier days, in quarters of your days."
            : $"Traffic on {r.DaysWithTraffic} of {Plural(r.RecordedDays, "recorded day")}. Darker squares are busier days, in quarters of your days.";
    }

    /// <summary>Weekday by hour of day, the "punch card" of when the connection is busy.</summary>
    private void BuildHours(UsageHistoryReport r)
    {
        HourRows.Clear();
        HourNote = null;
        if (!HasData || r.HourDetailDays == 0)
        {
            HourSummary = HasData ? $"Hour detail is kept for {_retention.HourBucketDays} days, and none of this range falls within it." : null;
            return;
        }

        double max = 0;
        for (int wd = 0; wd < 7; wd++)
            for (int h = 0; h < 24; h++)
                max = Math.Max(max, r.HourAverages[wd, h]);

        for (int wd = 0; wd < 7; wd++)
        {
            var cells = new List<HeatCell>(24);
            for (int h = 0; h < 24; h++)
            {
                double v = r.HourAverages[wd, h];
                double opacity = max <= 0 || v <= 0 ? 0 : 0.1 + 0.9 * (v / max);
                cells.Add(new HeatCell(opacity, IsBlank: false,
                    $"{WeekdayName(wd, false)}s, {h:00}:00–{(h + 1) % 24:00}:00\n{ByteFormat.Size(v)} on average", null));
            }
            HourRows.Add(new HeatRow(WeekdayName(wd, true), cells));
        }

        (int Day, int Hour) peak = (0, 0);
        for (int wd = 0; wd < 7; wd++)
            for (int h = 0; h < 24; h++)
                if (r.HourAverages[wd, h] > r.HourAverages[peak.Day, peak.Hour]) peak = (wd, h);

        HourSummary = $"The single busiest hour is {WeekdayName(peak.Day, false)}s from {peak.Hour:00}:00, " +
                      $"{ByteFormat.Size(r.HourAverages[peak.Day, peak.Hour])} on average.";

        if (r.HourDetailDays < r.RecordedDays)
            HourNote = $"Hour detail is kept for {_retention.HourBucketDays} days, so this uses the most recent " +
                       $"{Plural(r.HourDetailDays, "day")} of the range.";
    }

    // ---------------------------------------------------------------- applications

    private void BuildApps(UsageHistoryReport r)
    {
        var theme = _theme.Chart;
        var grouping = EffectiveGrouping(r);
        var top = r.Apps.Take(TopAppsCharted).ToList();
        var colours = top.Select((a, i) => (a.AppId, Colour: theme.Series[i % theme.Series.Count]))
            .ToDictionary(x => x.AppId, x => x.Colour);

        // Per bucket, per charted app, plus everything else.
        var index = _buckets.Select((b, i) => (b.Start, i)).ToDictionary(x => x.Start, x => x.i);
        int count = _buckets.Count;
        var perApp = top.ToDictionary(a => a.AppId, _ => new double[count]);
        var other = new double[count];

        foreach (var day in r.Data.Days)
        {
            int b = index[BucketOf(day.Day, grouping)];
            foreach (var (appId, value) in day.Apps)
            {
                long v = Pick(value.Sent, value.Received);
                if (perApp.TryGetValue(appId, out var values)) values[b] += v;
                else other[b] += v;
            }
        }

        string Tip(ChartPoint p) => ByteFormat.Size(p.Coordinate.PrimaryValue);

        var series = top.Select(a => (ISeries)new StackedColumnSeries<ObservablePoint>
        {
            Name = a.Name,
            Values = perApp[a.AppId].Select((v, i) => new ObservablePoint(i, v)).ToArray(),
            Fill = ChartPaints.Fill(colours[a.AppId]), Stroke = null, Padding = 1, MaxBarWidth = 40,
            YToolTipLabelFormatter = Tip
        }).ToList();

        if (other.Any(v => v > 0))
        {
            series.Add(new StackedColumnSeries<ObservablePoint>
            {
                Name = $"{Plural(r.Apps.Count - top.Count, "other application")}",
                Values = other.Select((v, i) => new ObservablePoint(i, v)).ToArray(),
                Fill = ChartPaints.Fill(theme.Muted, 150), Stroke = null, Padding = 1, MaxBarWidth = 40,
                YToolTipLabelFormatter = Tip
            });
        }

        AppSeries = [.. series];
        AppXAxes =
        [
            new Axis
            {
                Labeler = x => BucketLabel(x),
                MinStep = 1,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
        AppYAxes = [BytesAxis(theme)];

        AppRows.Clear();
        foreach (var app in r.Apps)
        {
            long measure = app.Measure(Direction);
            double share = r.Total > 0 ? measure / (double)r.Total : 0;
            var swatch = colours.TryGetValue(app.AppId, out var c) ? ToBrush(c) : ToBrush(theme.Muted.WithAlpha(150));

            string change = string.Empty;
            int direction = 0;
            double changeSort = 0;
            if (app.PreviousBytes is { } before)
            {
                if (before == 0)
                {
                    (change, direction, changeSort) = ("New", 1, double.MaxValue);
                }
                else
                {
                    double pct = (measure - (double)before) / before;
                    changeSort = pct;
                    if (Math.Abs(pct) < 0.03) change = "≈ same";
                    else (change, direction) = ($"{(pct > 0 ? "▲" : "▼")} {Math.Abs(pct) * 100:0}%", pct > 0 ? 1 : -1);
                }
            }

            AppRows.Add(new HistoryAppRow(
                swatch, app.Name, app.Received, app.Sent, measure, share, $"{share * 100:0.#}%",
                app.DaysActive, $"{app.BusiestDay:ddd d MMM} · {ByteFormat.Size(app.BusiestDayBytes)}",
                change, direction, changeSort));
        }

        if (r.Apps.Count == 0 || r.Total <= 0)
        {
            AppsSummary = null;
            return;
        }

        long topThree = r.Apps.Take(3).Sum(a => a.Measure(Direction));
        AppsSummary = r.Apps.Count <= 3
            ? $"{Plural(r.Apps.Count, "application")} used the network in this range."
            : $"{Plural(r.Apps.Count, "application")} used the network; the top three account for {Percent(topThree, r.Total)} of it.";
    }

    // ---------------------------------------------------------------- day log

    private void BuildDayRows(UsageHistoryReport r)
    {
        DayRows.Clear();
        var today = LocalDays.Today;

        foreach (var d in r.Days.AsEnumerable().Reverse())
        {
            string vs = string.Empty;
            double vsSort = double.MinValue;
            if (d.IsRecorded && r.DailyAverage > 0)
            {
                vsSort = (d.Measure - r.DailyAverage) / r.DailyAverage;
                vs = Math.Abs(vsSort) < 0.03 ? "≈ average" : $"{(vsSort > 0 ? "+" : "−")}{Math.Abs(vsSort) * 100:0}%";
            }

            var notes = new List<string>();
            if (!d.IsRecorded) notes.Add("Not recorded");
            else if (d.Sent + d.Received == 0 && d.Day != today) notes.Add("No traffic");
            if (d.Day == today) notes.Add("Today, so far");
            if (r.BusiestDay?.Day == d.Day && r.RecordedDays > 1) notes.Add("Busiest day");
            if (d.IsUnusual) notes.Add("Unusually heavy");

            string network = d.Problems > 0 ? Plural(d.Problems, "problem")
                : d.Changes > 0 ? Plural(d.Changes, "change")
                : string.Empty;

            DayRows.Add(new HistoryDayRow(
                d.Day,
                d.Day.ToString("ddd d MMM yyyy"),
                d.Received, d.Sent, d.Measure,
                vs, vsSort,
                d.TopAppId is { } app ? $"{AppName(app)} · {ByteFormat.Size(d.TopAppBytes)}" : string.Empty,
                d.BusiestHour is { } h ? $"{h:00}:00" : d.Measure > 0 ? "—" : string.Empty,
                network, d.Problems > 0,
                string.Join(" · ", notes),
                IsHighlighted: d.IsUnusual || (r.BusiestDay?.Day == d.Day && r.RecordedDays > 1),
                d.IsRecorded));
        }
    }

    // ================================================================ commands

    /// <summary>Nullable so heatmap squares outside the range can bind it without a date.</summary>
    [RelayCommand]
    private void OpenDay(DateOnly? day)
    {
        if (day is not { } value) return;
        _trends.ShowDay(value);
        _navigation.NavigateTo<GraphsViewModel>();
    }

    [RelayCommand]
    private void ExportDays()
    {
        if (_report is null) return;
        string? path = AskForCsvPath($"Cybernetic Bandwidth Monitor days {RangeFirst:yyyy-MM-dd} to {RangeLast:yyyy-MM-dd}.csv");
        if (path is null) return;

        var csv = new StringBuilder();
        csv.AppendLine("Date,Downloaded bytes,Uploaded bytes,Total bytes,Versus average,Top application,Top application bytes,Busiest hour,Network problems,Network changes,Note");
        foreach (var row in DayRows.Reverse())
        {
            var day = _report.Days.First(d => d.Day == row.Day);
            csv.AppendLine(string.Join(",",
                row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Downloaded.ToString(CultureInfo.InvariantCulture),
                row.Uploaded.ToString(CultureInfo.InvariantCulture),
                (row.Downloaded + row.Uploaded).ToString(CultureInfo.InvariantCulture),
                Csv(row.VsAverage),
                Csv(day.TopAppId is { } app ? AppName(app) : string.Empty),
                day.TopAppBytes.ToString(CultureInfo.InvariantCulture),
                Csv(row.BusiestHour),
                day.Problems.ToString(CultureInfo.InvariantCulture),
                day.Changes.ToString(CultureInfo.InvariantCulture),
                Csv(row.Note)));
        }
        WriteCsv(path, csv);
    }

    [RelayCommand]
    private void ExportApps()
    {
        if (_report is null) return;
        string? path = AskForCsvPath($"Cybernetic Bandwidth Monitor applications {RangeFirst:yyyy-MM-dd} to {RangeLast:yyyy-MM-dd}.csv");
        if (path is null) return;

        var csv = new StringBuilder();
        csv.AppendLine("Application,Downloaded bytes,Uploaded bytes,Total bytes,Share,Days used,Busiest day,Busiest day bytes,Previous period bytes");
        foreach (var app in _report.Apps)
        {
            csv.AppendLine(string.Join(",",
                Csv(app.Name),
                app.Received.ToString(CultureInfo.InvariantCulture),
                app.Sent.ToString(CultureInfo.InvariantCulture),
                (app.Sent + app.Received).ToString(CultureInfo.InvariantCulture),
                (_report.Total > 0 ? app.Measure(Direction) / (double)_report.Total : 0).ToString("0.####", CultureInfo.InvariantCulture),
                app.DaysActive.ToString(CultureInfo.InvariantCulture),
                app.BusiestDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                app.BusiestDayBytes.ToString(CultureInfo.InvariantCulture),
                app.PreviousBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }
        WriteCsv(path, csv);
    }

    private static string? AskForCsvPath(string fileName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv",
            AddExtension = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void WriteCsv(string path, StringBuilder csv)
    {
        try
        {
            // With a byte order mark, so Excel reads names with accents correctly.
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not export to {Path}.", path);
            Message = $"The export could not be saved: {ex.Message}";
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    // ---------------------------------------------------------------- range picker

    [RelayCommand]
    private void OpenPicker()
    {
        PendingFirst = RangeFirst;
        PendingLast = RangeLast;
        PickerMonth = new DateOnly(RangeLast.Year, RangeLast.Month, 1);
        BuildPicker();
        IsPickerOpen = true;
    }

    [RelayCommand]
    private void ApplyPreset(RangePreset preset)
    {
        var (first, last) = preset.Resolve(LocalDays.Today);
        SetRange(first, last);
        IsPickerOpen = false;
    }

    [RelayCommand]
    private void PickDay(PickerDay day)
    {
        if (!day.IsEnabled) return;

        if (PendingFirst is null || PendingLast is not null)
        {
            PendingFirst = day.Date;
            PendingLast = null;
        }
        else if (day.Date < PendingFirst)
        {
            (PendingFirst, PendingLast) = (day.Date, PendingFirst);
        }
        else
        {
            PendingLast = day.Date;
        }
        BuildPicker();
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        PickerMonth = PickerMonth.AddMonths(-1);
        BuildPicker();
    }

    [RelayCommand]
    private void NextMonth()
    {
        if (PickerMonth.AddMonths(1) > LocalDays.Today) return;
        PickerMonth = PickerMonth.AddMonths(1);
        BuildPicker();
    }

    private bool CanApplyCustomRange() => PendingFirst is not null && PendingLast is not null;

    [RelayCommand(CanExecute = nameof(CanApplyCustomRange))]
    private void ApplyCustomRange()
    {
        SetRange(PendingFirst!.Value, PendingLast!.Value);
        IsPickerOpen = false;
    }

    [RelayCommand]
    private void ClosePicker() => IsPickerOpen = false;

    private void SetRange(DateOnly first, DateOnly last)
    {
        var today = LocalDays.Today;
        if (last > today) last = today;
        if (first < MinDay) first = MinDay;
        if (first > last) first = last;

        RangeFirst = first;
        RangeLast = last;
        _ = ReloadAsync();
    }

    private void BuildPicker()
    {
        var today = LocalDays.Today;
        var start = PickerMonth.AddDays(-UsageHistoryReport.MondayIndex(PickerMonth.DayOfWeek));
        var first = PendingFirst;
        var last = PendingLast ?? PendingFirst;

        PickerMonthLabel = PickerMonth.ToString("MMMM yyyy");
        PickerDays.Clear();
        for (int i = 0; i < PickerCellCount; i++)
        {
            var date = start.AddDays(i);
            PickerDays.Add(new PickerDay(
                date,
                date.Day.ToString(CultureInfo.CurrentCulture),
                IsInMonth: date.Month == PickerMonth.Month,
                IsEnabled: date >= MinDay && date <= today,
                IsInRange: first is { } f && last is { } l && date >= f && date <= l,
                IsEdge: date == first || date == last,
                IsToday: date == today,
                HasData: _daysWithData.Contains(date)));
        }

        PickerHint = PendingFirst is null ? "Click the first day of the range."
            : PendingLast is null ? $"From {PendingFirst:d MMM yyyy}. Now click the last day."
            : $"{FormatRange(PendingFirst.Value, PendingLast.Value)} · {Plural(PendingLast.Value.DayNumber - PendingFirst.Value.DayNumber + 1, "day")}";
    }

    // ================================================================ helpers

    private long Pick(long sent, long received) => Direction switch
    {
        Direction.Upload => sent,
        Direction.Download => received,
        _ => sent + received
    };

    private string AppName(long appId) => _current?.AppNames.GetValueOrDefault(appId) ?? "Unknown application";

    private string? PercentChange(double current, double? previous)
    {
        if (previous is not > 0) return null;
        double change = (current - previous.Value) / previous.Value;
        return Math.Abs(change) < 0.03
            ? $"≈ same as {Versus}"
            : $"{(change > 0 ? "▲" : "▼")} {Math.Abs(change) * 100:0}% vs {Versus}";
    }

    private static string Percent(double part, double whole) => whole <= 0 ? "0%" : $"{part / whole * 100:0}%";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    /// <summary>Monday is 0.</summary>
    private static string WeekdayName(int mondayIndex, bool abbreviated)
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        int dayOfWeek = (mondayIndex + 1) % 7;
        return abbreviated ? format.AbbreviatedDayNames[dayOfWeek] : format.DayNames[dayOfWeek];
    }

    private static string FormatRange(DateOnly first, DateOnly last) =>
        first == last ? last.ToString("d MMM yyyy")
        : first.Year != last.Year ? $"{first:d MMM yyyy} – {last:d MMM yyyy}"
        : first.Month != last.Month ? $"{first:d MMM} – {last:d MMM yyyy}"
        : $"{first.Day} – {last:d MMM yyyy}";

    private static Axis BytesAxis(ChartTheme theme) => new()
    {
        MinLimit = 0,
        Labeler = ByteFormat.Size,
        LabelsPaint = ChartPaints.Labels(theme),
        SeparatorsPaint = ChartPaints.Separators(theme)
    };

    private static SolidColorBrush ToBrush(SKColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }

    private void ApplyChrome()
    {
        TooltipTextPaint = ChartPaints.Text(_theme.Chart);
        TooltipBackgroundPaint = ChartPaints.TooltipBackground(_theme.Chart);
    }
}
