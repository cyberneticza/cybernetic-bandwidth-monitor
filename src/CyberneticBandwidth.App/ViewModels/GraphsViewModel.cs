using System.Collections.ObjectModel;
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
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;
using SkiaSharp;

namespace CyberneticBandwidth.App.ViewModels;

public sealed record WindowOption(TimeWindow Value, string Label);

public sealed record DirectionOption(Direction Value, string Label);

/// <summary>Detail bin size. Null is Auto: the finest the stored data can honestly fill.</summary>
public sealed record ResolutionOption(TimeSpan? Value, string Label);

/// <summary>One application's line in the day chart's hover panel.</summary>
public sealed record HoverRow(Brush Swatch, string Name, string Value);

public sealed partial class DayAppRow(long appId, string displayName, long received, long sent, Action changed)
    : ObservableObject
{
    public long AppId { get; } = appId;
    public string DisplayName { get; } = displayName;
    public long BytesReceived { get; } = received;
    public long BytesSent { get; } = sent;

    /// <summary>The colour this app has on the detail chart; transparent when folded into "other".</summary>
    [ObservableProperty] private Brush _swatch = Brushes.Transparent;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Trends: a daily overview of the chosen period on top, and the selected day in detail below.
///
/// Days are local calendar days, while buckets are stored in UTC, so the overview is assembled
/// from hour buckets (kept four months) and only falls back to the stored UTC day buckets for
/// older history. The detail chart uses minute buckets (kept a week) where they cover the day and
/// says so when it cannot.
/// </summary>
public sealed partial class GraphsViewModel : ObservableObject
{
    private const int DetailSeriesLimit = 8;
    private const double CompleteEnough = 0.97;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ThemeService _theme;
    private readonly RetentionOptions _retention;
    private readonly ILogger<GraphsViewModel> _log;

    private List<DayTotal> _days = [];
    private IReadOnlyList<UsageSeries> _dayApps = [];
    private IReadOnlyList<NetworkEventRecord> _dayEvents = [];
    private DateTimeOffset _dayStart;
    private TimeSpan _dayBin = Hour;
    private bool _loadingOverview;
    private int _dayRequest;
    private bool _suppressDetailRebuild;

    // What the hover panel reads: each charted app's figures per slot of the selected day.
    private readonly record struct HoverSeries(string Name, Brush Swatch, long[] Sent, long[] Received);
    private IReadOnlyList<HoverSeries> _hoverSeries = [];
    private int _hoverSlot = -1;

    private readonly record struct DayTotal(DateOnly Day, long Sent, long Received);

    [ObservableProperty] private TimeWindow _selectedWindow = TimeWindow.Month1;
    [ObservableProperty] private Direction _direction = Direction.Both;
    [ObservableProperty] private ResolutionOption _selectedResolution;
    [ObservableProperty] private bool _isStacked = true;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _overviewMessage;
    [ObservableProperty] private long _periodBytesReceived;
    [ObservableProperty] private long _periodBytesSent;
    [ObservableProperty] private string _periodLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDayLabel), nameof(IsToday))]
    [NotifyCanExecuteChangedFor(nameof(NextDayCommand), nameof(TodayCommand))]
    private DateOnly _selectedDay = LocalDays.Today;

    [ObservableProperty] private long _dayBytesReceived;
    [ObservableProperty] private long _dayBytesSent;
    [ObservableProperty] private string _detailResolutionLabel = string.Empty;
    [ObservableProperty] private string? _detailMessage;

    [ObservableProperty] private ISeries[] _overviewSeries = [];
    [ObservableProperty] private Axis[] _overviewXAxes = [];
    [ObservableProperty] private Axis[] _overviewYAxes = [];
    [ObservableProperty] private RectangularSection[] _overviewSections = [];

    [ObservableProperty] private ISeries[] _detailSeries = [];
    [ObservableProperty] private Axis[] _detailXAxes = [];
    [ObservableProperty] private Axis[] _detailYAxes = [];
    [ObservableProperty] private RectangularSection[] _detailSections = [];
    [ObservableProperty] private string? _dayEventsText;
    [ObservableProperty] private bool _dayHasProblems;

    // The day chart's own hover panel. LiveCharts draws its tooltip inside the chart, where a
    // day with many applications runs off the bottom; this one is a popup and never clips.
    [ObservableProperty] private bool _isHoverOpen;
    [ObservableProperty] private string _hoverTitle = string.Empty;
    [ObservableProperty] private string _hoverTotals = string.Empty;
    [ObservableProperty] private string? _hoverFootnote;

    public ObservableCollection<HoverRow> HoverRows { get; } = [];

    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = null!;
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = null!;

    public IReadOnlyList<WindowOption> TimeWindows { get; } = Enum.GetValues<TimeWindow>()
        .Select(w => new WindowOption(w, w.Label()))
        .ToList();

    public IReadOnlyList<DirectionOption> Directions { get; } =
    [
        new(Direction.Both, "Up and down"),
        new(Direction.Download, "Downloaded"),
        new(Direction.Upload, "Uploaded")
    ];

    public IReadOnlyList<ResolutionOption> Resolutions { get; } =
    [
        new(null, "Auto"),
        new(TimeSpan.FromMinutes(1), "1 minute"),
        new(TimeSpan.FromMinutes(5), "5 minutes"),
        new(TimeSpan.FromMinutes(15), "15 minutes"),
        new(Hour, "Hourly")
    ];

    public ObservableCollection<DayAppRow> DayApps { get; } = [];

    public string SelectedDayLabel => SelectedDay.ToString("dddd, d MMMM yyyy");

    public bool IsToday => SelectedDay == LocalDays.Today;

    public GraphsViewModel(
        IServiceScopeFactory scopes,
        ThemeService theme,
        IOptions<RetentionOptions> retention,
        ILogger<GraphsViewModel> log)
    {
        _scopes = scopes;
        _theme = theme;
        _retention = retention.Value;
        _log = log;
        _selectedResolution = Resolutions[0];

        ApplyChartChrome();
        theme.ThemeChanged += () =>
        {
            ApplyChartChrome();
            BuildOverview();
            BuildDetail();
        };
    }

    partial void OnSelectedWindowChanged(TimeWindow value) => _ = ReloadAsync();

    partial void OnDirectionChanged(Direction value)
    {
        BuildOverview();
        SortDayApps();
        BuildDetail();
    }

    partial void OnIsStackedChanged(bool value) => BuildDetail();

    partial void OnSelectedResolutionChanged(ResolutionOption value) => _ = LoadDayAsync();

    // ================================================================ commands

    /// <summary>Everything from the store: the period's days, then the selected day.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (_loadingOverview) return;
        _loadingOverview = true;
        IsBusy = true;

        try
        {
            // The app may have been open across midnight; never leave the selection in the future.
            if (SelectedDay > LocalDays.Today) SelectedDay = LocalDays.Today;

            _days = await LoadDaysAsync(SelectedWindow);
            SummarisePeriod();
            BuildOverview();

            OverviewMessage = _days.All(d => d.Sent + d.Received == 0)
                ? "No usage recorded for this period yet. Leave the app running and check back."
                : null;

            await LoadDayAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load the trends view.");
            OverviewMessage = "The usage history could not be read. The log file has the details.";
        }
        finally
        {
            _loadingOverview = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void PreviousDay() => SelectDay(SelectedDay.AddDays(-1));

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextDay() => SelectDay(SelectedDay.AddDays(1));

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Today() => SelectDay(LocalDays.Today);

    private bool CanGoForward() => SelectedDay < LocalDays.Today;

    /// <summary>
    /// Selects <paramref name="day"/> for the next time the page loads, as when another page opens a
    /// day here. Widens the period when the day falls before it, so the overview shows the day too.
    /// </summary>
    public void ShowDay(DateOnly day)
    {
        if (day > LocalDays.Today) day = LocalDays.Today;
        SelectedDay = day;
        UpdateSelectionSection();

        int needed = LocalDays.Today.DayNumber - day.DayNumber + 1;
        if (needed > SelectedWindow.Days())
        {
            SelectedWindow = Enum.GetValues<TimeWindow>()
                .Where(w => w.Days() >= needed)
                .DefaultIfEmpty(TimeWindow.Year1)
                .First();
        }
    }

    [RelayCommand]
    private void ResetOverviewZoom() => ResetZoom(OverviewXAxes);

    [RelayCommand]
    private void ResetDetailZoom() => ResetZoom(DetailXAxes);

    private void SelectDay(DateOnly day)
    {
        if (day > LocalDays.Today) day = LocalDays.Today;
        if (day == SelectedDay && _dayApps.Count > 0) return;

        SelectedDay = day;
        UpdateSelectionSection();
        _ = LoadDayAsync();
    }

    private static void ResetZoom(Axis[] axes)
    {
        foreach (var axis in axes)
        {
            axis.MinLimit = null;
            axis.MaxLimit = null;
        }
    }

    // ================================================================ overview

    private async Task<List<DayTotal>> LoadDaysAsync(TimeWindow window)
    {
        var today = LocalDays.Today;
        var firstDay = today.AddDays(-(window.Days() - 1));
        var from = LocalDays.StartOf(firstDay);
        var to = LocalDays.EndOf(today);

        // Hours survive HourBucketDays; before that only UTC day buckets remain.
        var hourlyFrom = LocalDays.StartOf(today.AddDays(-(_retention.HourBucketDays - 1)));
        if (hourlyFrom < from) hourlyFrom = from;

        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

        var byDay = new Dictionary<DateOnly, (long Sent, long Received)>();

        foreach (var point in await repo.GetTimelineAsync(hourlyFrom, to, Hour, CancellationToken.None))
        {
            var day = LocalDays.Of(point.BucketStartUtc);
            byDay.TryGetValue(day, out var sum);
            byDay[day] = (sum.Sent + point.BytesSent, sum.Received + point.BytesReceived);
        }

        if (from < hourlyFrom)
        {
            // A stored day bucket is a UTC day. Filing it under its UTC date is off by the zone's
            // offset at the edges, which is as close as history this old can get.
            foreach (var point in await repo.GetTimelineAsync(from, hourlyFrom, Day, CancellationToken.None))
            {
                var day = DateOnly.FromDateTime(point.BucketStartUtc.UtcDateTime);
                if (!byDay.ContainsKey(day)) byDay[day] = (point.BytesSent, point.BytesReceived);
            }
        }

        var days = new List<DayTotal>(window.Days());
        for (var day = firstDay; day <= today; day = day.AddDays(1))
        {
            byDay.TryGetValue(day, out var sum);
            days.Add(new DayTotal(day, sum.Sent, sum.Received));
        }
        return days;
    }

    private void SummarisePeriod()
    {
        PeriodBytesReceived = _days.Sum(d => d.Received);
        PeriodBytesSent = _days.Sum(d => d.Sent);

        // Averaged over days that recorded anything: days before the first install would
        // otherwise drag the figure towards zero.
        var active = _days.Where(d => d.Sent + d.Received > 0).ToList();
        PeriodLabel = active.Count == 0
            ? SelectedWindow.Label()
            : $"{SelectedWindow.Label()} · {ByteFormat.Size(active.Average(d => (double)(d.Sent + d.Received)))} a day on average";
    }

    private void BuildOverview()
    {
        var theme = _theme.Chart;

        // X is the day's index in the period, not a timestamp. The axis picks "nice" steps in
        // whatever unit X is in; in DateTime ticks those land at odd hours and labels repeat or
        // skip dates, in whole days they are 1, 2, 5, 10 days.
        ObservablePoint[] Values(Func<DayTotal, long> value) =>
            _days.Select((d, i) => new ObservablePoint(i, value(d))).ToArray();

        string DayTooltip(ChartPoint p) => ByteFormat.Size(p.Coordinate.PrimaryValue);

        ISeries Column(string name, SKColor colour, Func<DayTotal, long> value, bool stacked)
        {
            if (stacked)
            {
                var s = new StackedColumnSeries<ObservablePoint>
                {
                    Name = name, Values = Values(value), Fill = ChartPaints.Fill(colour), Stroke = null,
                    Padding = 1, MaxBarWidth = 48, Rx = 2, Ry = 2, YToolTipLabelFormatter = DayTooltip
                };
                s.DataPointerDown += (_, points) => OnDayClicked(points.FirstOrDefault()?.Model);
                return s;
            }

            var c = new ColumnSeries<ObservablePoint>
            {
                Name = name, Values = Values(value), Fill = ChartPaints.Fill(colour), Stroke = null,
                Padding = 1, MaxBarWidth = 48, Rx = 2, Ry = 2, YToolTipLabelFormatter = DayTooltip
            };
            c.DataPointerDown += (_, points) => OnDayClicked(points.FirstOrDefault()?.Model);
            return c;
        }

        // Stacked when both directions show: the bar's height is the day's total, split by direction.
        OverviewSeries = Direction switch
        {
            Direction.Download => [Column("Downloaded", theme.Download, d => d.Received, stacked: false)],
            Direction.Upload => [Column("Uploaded", theme.Upload, d => d.Sent, stacked: false)],
            _ =>
            [
                Column("Downloaded", theme.Download, d => d.Received, stacked: true),
                Column("Uploaded", theme.Upload, d => d.Sent, stacked: true)
            ]
        };

        OverviewXAxes =
        [
            new Axis
            {
                Labeler = DayLabel,
                MinStep = 1,
                TextSize = ChartScale.LabelSize,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];

        OverviewYAxes =
        [
            new Axis
            {
                Labeler = ByteFormat.Size,
                TextSize = ChartScale.LabelSize,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = ChartPaints.Separators(theme)
            }
        ];
        ChartScale.Bytes(OverviewYAxes[0], ChartScale.MaxOf(OverviewSeries));

        UpdateSelectionSection();
    }

    private string DayLabel(double index)
    {
        int i = (int)Math.Round(index);
        return i >= 0 && i < _days.Count ? _days[i].Day.ToString("d MMM") : string.Empty;
    }

    private void OnDayClicked(ObservablePoint? point)
    {
        if (point?.X is not { } x) return;
        int i = (int)Math.Round(x);
        if (i >= 0 && i < _days.Count) SelectDay(_days[i].Day);
    }

    /// <summary>A band behind the selected day's column, so the overview shows what the detail is.</summary>
    private void UpdateSelectionSection()
    {
        int index = _days.FindIndex(d => d.Day == SelectedDay);
        OverviewSections = index < 0
            ? []
            :
            [
                new RectangularSection
                {
                    Xi = index - 0.5,
                    Xj = index + 0.5,
                    Fill = ChartPaints.Fill(_theme.Chart.Muted, 44)
                }
            ];
    }

    // ================================================================ detail

    private async Task LoadDayAsync()
    {
        int request = ++_dayRequest;
        var day = SelectedDay;
        var start = LocalDays.StartOf(day);
        var end = LocalDays.EndOf(day);

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

            string? message = null;
            TimeSpan bin;
            IReadOnlyList<UsageSeries> apps;

            var hourly = await repo.GetAppTimelineAsync(start, end, Hour, CancellationToken.None);
            long hourlyTotal = TotalOf(hourly);

            if (hourlyTotal == 0 && day < LocalDays.Today.AddDays(-(_retention.HourBucketDays - 1)))
            {
                apps = await repo.GetAppTimelineAsync(start, end, Day, CancellationToken.None);
                bin = Day;
                message = $"Only daily totals are kept for days older than {_retention.HourBucketDays} days.";
            }
            else
            {
                var requested = SelectedResolution.Value;
                bool minutesMayExist = day >= LocalDays.Today.AddDays(-_retention.MinuteBucketDays);

                if (requested is { } fixedBin && fixedBin < Hour)
                {
                    apps = await repo.GetAppTimelineAsync(start, end, fixedBin, CancellationToken.None);
                    bin = fixedBin;
                    if (TotalOf(apps) < hourlyTotal * CompleteEnough)
                    {
                        message = minutesMayExist
                            ? "Minute detail covers only part of this day, from when the app was recording. Hourly shows all of it."
                            : $"Minute detail is kept for {_retention.MinuteBucketDays} days. Hourly shows this day in full.";
                    }
                }
                else if (requested is null && minutesMayExist)
                {
                    var fine = await repo.GetAppTimelineAsync(start, end, TimeSpan.FromMinutes(5), CancellationToken.None);
                    bool complete = TotalOf(fine) > 0 && TotalOf(fine) >= hourlyTotal * CompleteEnough;
                    (apps, bin) = complete ? (fine, TimeSpan.FromMinutes(5)) : (hourly, Hour);
                }
                else
                {
                    (apps, bin) = (hourly, Hour);
                }
            }

            var events = await scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>()
                .GetNetworkEventsAsync(start, end, CancellationToken.None);

            // A later click has already asked for another day; drop this result.
            if (request != _dayRequest) return;

            _dayEvents = events;
            int problems = events.Count(e => e.IsProblem);
            DayHasProblems = problems > 0;
            DayEventsText = events.Count == 0 ? null
                : problems == 0 ? $"{events.Count} network {(events.Count == 1 ? "change" : "changes")}"
                : $"{problems} network {(problems == 1 ? "problem" : "problems")}";

            _dayApps = apps;
            _dayStart = start;
            _dayBin = bin;

            DayBytesReceived = apps.Sum(a => a.Points.Sum(p => p.BytesReceived));
            DayBytesSent = apps.Sum(a => a.Points.Sum(p => p.BytesSent));
            DetailResolutionLabel = bin switch
            {
                _ when bin >= Day => "daily total",
                _ when bin >= Hour => "hourly detail",
                _ => $"{bin.TotalMinutes:0}-minute detail"
            };
            DetailMessage = message;

            RebuildDayApps();
            BuildDetail();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load usage for {Day}.", day);
            if (request == _dayRequest) DetailMessage = "This day could not be read. The log file has the details.";
        }
    }

    private static long TotalOf(IReadOnlyList<UsageSeries> apps) =>
        apps.Sum(a => a.Points.Sum(p => p.BytesSent + p.BytesReceived));

    private void RebuildDayApps()
    {
        var ticked = DayApps.Where(r => r.IsChecked).Select(r => r.AppId).ToHashSet();

        _suppressDetailRebuild = true;
        DayApps.Clear();
        foreach (var app in _dayApps)
        {
            DayApps.Add(new DayAppRow(
                app.AppId,
                app.DisplayName,
                app.Points.Sum(p => p.BytesReceived),
                app.Points.Sum(p => p.BytesSent),
                BuildDetail)
            {
                IsChecked = ticked.Contains(app.AppId)
            });
        }
        _suppressDetailRebuild = false;

        SortDayApps();
    }

    private void SortDayApps()
    {
        var sorted = DayApps.OrderByDescending(r => Measure(r.BytesSent, r.BytesReceived)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = DayApps.IndexOf(sorted[i]);
            if (current != i) DayApps.Move(current, i);
        }
    }

    private long Measure(long sent, long received) => Direction switch
    {
        Direction.Upload => sent,
        Direction.Download => received,
        _ => sent + received
    };

    private void BuildDetail()
    {
        if (_suppressDetailRebuild) return;

        var theme = _theme.Chart;
        var direction = Direction;
        var ticked = DayApps.Where(r => r.IsChecked).Select(r => r.AppId).ToHashSet();

        var candidates = _dayApps
            .Where(a => ticked.Count == 0 || ticked.Contains(a.AppId))
            .OrderByDescending(a => a.Points.Sum(p => p.For(direction)))
            .ToList();

        var shown = candidates.Take(DetailSeriesLimit).ToList();
        var folded = ticked.Count == 0 ? candidates.Skip(DetailSeriesLimit).ToList() : [];

        // Dense grid of bins across the whole local day: stacked series align by index, and a
        // missing bin has to be a zero rather than a gap that shifts everything after it.
        // X is hours elapsed since local midnight, so axis steps come out as whole hours or clean
        // fractions of one (30, 12, 6 minutes) instead of arbitrary tick counts.
        long binTicks = _dayBin.Ticks;
        double binHours = _dayBin.TotalHours;
        int slots = Math.Max(1, (int)Math.Round((LocalDays.EndOf(SelectedDay) - _dayStart).Ticks / (double)binTicks));

        ObservablePoint[] Densify(IEnumerable<UsageSeries> series)
        {
            var sums = new long[slots];
            foreach (var s in series)
            {
                foreach (var p in s.Points)
                {
                    int i = (int)((p.BucketStartUtc - _dayStart).Ticks / binTicks);
                    if (i >= 0 && i < slots) sums[i] += p.For(direction);
                }
            }
            return sums.Select((v, i) => new ObservablePoint(i * binHours, v)).ToArray();
        }

        var colours = new Dictionary<long, SKColor>();
        var series = new List<ISeries>();

        (long[] Sent, long[] Received) Slots(UsageSeries app)
        {
            var sent = new long[slots];
            var received = new long[slots];
            foreach (var p in app.Points)
            {
                int i = (int)((p.BucketStartUtc - _dayStart).Ticks / binTicks);
                if (i < 0 || i >= slots) continue;
                sent[i] += p.BytesSent;
                received[i] += p.BytesReceived;
            }
            return (sent, received);
        }

        for (int i = 0; i < shown.Count; i++)
        {
            var colour = theme.Series[i % theme.Series.Count];
            colours[shown[i].AppId] = colour;
            series.Add(MakeDetailSeries(shown[i].DisplayName, Densify([shown[i]]), colour));
        }

        if (folded.Count > 0)
            series.Add(MakeDetailSeries($"{folded.Count} other applications", Densify(folded), theme.Muted));

        DetailSeries = [.. series];

        // Every candidate app, not only the charted eight: the panel is where the ones folded
        // into "other applications" can still be read individually.
        var muted = ToBrush(theme.Muted);
        _hoverSeries = candidates
            .Select(app =>
            {
                var (sent, received) = Slots(app);
                var swatch = colours.TryGetValue(app.AppId, out var c) ? ToBrush(c) : muted;
                return new HoverSeries(app.DisplayName, swatch, sent, received);
            })
            .ToList();
        EndHover();

        // Network events as hairline markers behind the bars, so a dip in traffic can be read against
        // a drop or a network switch at the same moment, without covering the traffic. Problems in the
        // alert colour. No captions on the chart: they collided with each other and with the chart's
        // edge; the header counts the problems and the hover panel names each event.
        DetailSections = _dayEvents
            .Where(e => e.Kind is not NetworkEventKind.ConnectionRecovered)
            .Select(e =>
            {
                double x = (e.ObservedUtc - _dayStart).TotalHours;
                return new RectangularSection
                {
                    Xi = x,
                    Xj = x,
                    Stroke = new SolidColorPaint((e.IsProblem ? theme.Alert : theme.Muted).WithAlpha(170))
                    {
                        StrokeThickness = 1.5f,
                        PathEffect = new DashEffect([4, 3])
                    },
                    ZIndex = -1
                };
            })
            .ToArray();

        foreach (var row in DayApps)
        {
            row.Swatch = colours.TryGetValue(row.AppId, out var c) ? ToBrush(c) : Brushes.Transparent;
        }

        var dayStart = _dayStart;
        string timeFormat = _dayBin >= Day ? "d MMM" : "HH:mm";
        DetailXAxes =
        [
            new Axis
            {
                // Through the offset-aware start, so a DST day's labels follow the wall clock.
                Labeler = hours => hours is < 0 or > 25
                    ? string.Empty
                    : dayStart.AddHours(hours).ToLocalTime().ToString(timeFormat),
                UnitWidth = binHours,
                MinStep = binHours,
                TextSize = ChartScale.LabelSize,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];

        DetailYAxes =
        [
            new Axis
            {
                Labeler = ByteFormat.Size,
                TextSize = ChartScale.LabelSize,
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = ChartPaints.Separators(theme)
            }
        ];
        ChartScale.Bytes(DetailYAxes[0], ChartScale.MaxOf(DetailSeries));
    }

    private ISeries MakeDetailSeries(string name, ObservablePoint[] values, SKColor colour)
    {
        string Tooltip(ChartPoint p) => ByteFormat.Size(p.Coordinate.PrimaryValue);

        // Hourly bins read best as columns; finer bins are too many to draw as bars.
        if (_dayBin >= Hour)
        {
            if (IsStacked)
                return new StackedColumnSeries<ObservablePoint>
                {
                    Name = name, Values = values, Fill = ChartPaints.Fill(colour), Stroke = null,
                    Padding = 2, Rx = 1, Ry = 1, YToolTipLabelFormatter = Tooltip
                };

            return new ColumnSeries<ObservablePoint>
            {
                Name = name, Values = values, Fill = ChartPaints.Fill(colour), Stroke = null,
                Padding = 2, Rx = 1, Ry = 1, YToolTipLabelFormatter = Tooltip
            };
        }

        if (IsStacked)
            return new StackedAreaSeries<ObservablePoint>
            {
                Name = name, Values = values, GeometrySize = 0, LineSmoothness = 0,
                Fill = ChartPaints.Fill(colour, 200), Stroke = ChartPaints.Stroke(colour, 1),
                YToolTipLabelFormatter = Tooltip
            };

        return new LineSeries<ObservablePoint>
        {
            Name = name, Values = values, GeometrySize = 0, LineSmoothness = 0,
            Fill = null, Stroke = ChartPaints.Stroke(colour, 1.5f),
            YToolTipLabelFormatter = Tooltip
        };
    }

    // ================================================================ hover

    private const int HoverRowLimit = 12;

    /// <summary>
    /// Fills the hover panel for the slot under the pointer. <paramref name="hours"/> is the chart's
    /// X value: hours since the start of the selected day.
    /// </summary>
    public void HoverAt(double hours)
    {
        double binHours = _dayBin.TotalHours;
        int slot = (int)Math.Floor(hours / binHours);
        int slots = _hoverSeries.Count == 0 ? 0 : _hoverSeries[0].Sent.Length;

        if (hours < 0 || slot >= slots)
        {
            EndHover();
            return;
        }
        if (slot == _hoverSlot && IsHoverOpen) return;
        _hoverSlot = slot;

        var from = _dayStart.AddHours(slot * binHours).ToLocalTime();
        var to = _dayStart.AddHours((slot + 1) * binHours).ToLocalTime();
        HoverTitle = _dayBin >= Day ? from.ToString("dddd d MMMM") : $"{from:HH:mm} – {to:HH:mm}";

        var direction = Direction;
        long Measure(HoverSeries s) => direction switch
        {
            Direction.Upload => s.Sent[slot],
            Direction.Download => s.Received[slot],
            _ => s.Sent[slot] + s.Received[slot]
        };

        var active = _hoverSeries.Where(s => Measure(s) > 0).OrderByDescending(Measure).ToList();
        long down = _hoverSeries.Sum(s => s.Received[slot]);
        long up = _hoverSeries.Sum(s => s.Sent[slot]);
        HoverTotals = $"↓ {ByteFormat.Size(down)}   ↑ {ByteFormat.Size(up)}";

        HoverRows.Clear();
        foreach (var s in active.Take(HoverRowLimit))
            HoverRows.Add(new HoverRow(s.Swatch, s.Name, ByteFormat.Size(Measure(s))));

        var notes = new List<string>();
        if (active.Count > HoverRowLimit)
        {
            var rest = active.Skip(HoverRowLimit).ToList();
            notes.Add($"{rest.Count} more applications, {ByteFormat.Size(rest.Sum(Measure))}");
        }
        if (active.Count == 0) notes.Add("No traffic recorded in this slot.");

        var fromUtc = _dayStart.AddHours(slot * binHours);
        var toUtc = _dayStart.AddHours((slot + 1) * binHours);
        notes.AddRange(_dayEvents
            .Where(e => e.ObservedUtc >= fromUtc && e.ObservedUtc < toUtc)
            .Select(e => $"{e.ObservedUtc.ToLocalTime():HH:mm}  {e.Title}"));

        HoverFootnote = notes.Count == 0 ? null : string.Join("\n", notes);
        IsHoverOpen = true;
    }

    public void EndHover()
    {
        _hoverSlot = -1;
        IsHoverOpen = false;
    }

    private static SolidColorBrush ToBrush(SKColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }

    // ================================================================ shared

    private void ApplyChartChrome()
    {
        TooltipTextPaint = ChartPaints.Text(_theme.Chart);
        TooltipBackgroundPaint = ChartPaints.TooltipBackground(_theme.Chart);
    }
}
