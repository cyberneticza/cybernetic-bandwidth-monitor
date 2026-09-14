using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.ViewModels;

/// <summary>
/// The live view shows two separate truths side by side rather than pretending they are one:
/// byte counts come from NDU and are attributed to applications, while the socket list comes
/// from the connection table and is keyed by PID. The two name spaces do not line up reliably,
/// so joining them would invent precision that is not there.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int HistorySeconds = 300;

    private readonly IconCache _icons;
    private readonly MonitoringSettings _monitoring;
    private readonly HealthOptions _healthOptions;
    private readonly NavigationRequests _navigation;
    private readonly LineSeries<DateTimePoint> _download;
    private readonly LineSeries<DateTimePoint> _upload;
    private readonly Dictionary<string, LiveAppRowViewModel> _appIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Last value seen for each (app, interface, bucket). The sampler deliberately re-reads
    /// overlapping minutes, so accumulating raw samples would double count — deltas against this
    /// are correct.
    /// </summary>
    private readonly Dictionary<(string App, InterfaceKind Interface, long Bucket), (long Sent, long Received)> _seen = new();

    [ObservableProperty] private double _downloadBytesPerSecond;
    [ObservableProperty] private double _uploadBytesPerSecond;
    [ObservableProperty] private int _activeSocketCount;
    [ObservableProperty] private int _processCount;

    /// <summary>
    /// Since local midnight, all applications. Null until the first budget check completes: that
    /// re-reads the store after every usage sample, and the store includes everything since midnight,
    /// which predates this session whenever the app starts mid-day.
    /// </summary>
    [ObservableProperty] private long? _todayBytesReceived;
    [ObservableProperty] private long? _todayBytesSent;

    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = null!;
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = null!;

    // The connection card
    [ObservableProperty] private string _connectionName = "Not connected";
    [ObservableProperty] private string _connectionGlyph = NetworkDisplay.MediumGlyph(ConnectionMedium.None);
    [ObservableProperty] private string _connectionDetail = string.Empty;
    [ObservableProperty] private string? _routesText;
    [ObservableProperty] private HealthStatus _healthStatus = HealthStatus.Unknown;
    [ObservableProperty] private string _healthText = NetworkDisplay.StatusName(HealthStatus.Unknown);
    [ObservableProperty] private string _healthMeaning = string.Empty;

    // Budgets, shown under today's total only when set
    [ObservableProperty] private double? _dailyBudgetFraction;
    [ObservableProperty] private string? _dailyBudgetText;
    [ObservableProperty] private double? _monthlyBudgetFraction;
    [ObservableProperty] private string? _monthlyBudgetText;

    public ObservableCollection<LiveAppRowViewModel> Apps { get; } = [];
    public ObservableCollection<SocketRowViewModel> Sockets { get; } = [];

    public ObservableCollection<DateTimePoint> DownloadPoints { get; } = [];
    public ObservableCollection<DateTimePoint> UploadPoints { get; } = [];

    public ISeries[] ThroughputSeries { get; }
    public Axis[] ThroughputXAxes { get; }
    public Axis[] ThroughputYAxes { get; }

    public DashboardViewModel(
        LiveActivityBroker broker,
        IconCache icons,
        ThemeService theme,
        UsageAlertService budgets,
        MonitoringSettings monitoring,
        IOptions<HealthOptions> healthOptions,
        NavigationRequests navigation)
    {
        _icons = icons;
        _monitoring = monitoring;
        _healthOptions = healthOptions.Value;
        _navigation = navigation;

        // Upload is plotted below the axis. A mirrored band is the standard way to read a link
        // at a glance: shape above the line is what came in, shape below is what went out.
        _download = new LineSeries<DateTimePoint>
        {
            Name = "Download",
            Values = DownloadPoints,
            GeometrySize = 0,
            LineSmoothness = 0.35,
            YToolTipLabelFormatter = p => ByteFormat.Rate(p.Coordinate.PrimaryValue)
        };
        _upload = new LineSeries<DateTimePoint>
        {
            Name = "Upload",
            Values = UploadPoints,
            GeometrySize = 0,
            LineSmoothness = 0.35,
            YToolTipLabelFormatter = p => ByteFormat.Rate(System.Math.Abs(p.Coordinate.PrimaryValue))
        };
        ThroughputSeries = [_download, _upload];

        ThroughputXAxes =
        [
            new Axis
            {
                Labeler = value => SafeTicksToTime(value),
                UnitWidth = TimeSpan.FromSeconds(1).Ticks,
                MinStep = TimeSpan.FromSeconds(30).Ticks,
                SeparatorsPaint = null
            }
        ];

        ThroughputYAxes =
        [
            new Axis
            {
                // Symmetric about zero (see OnThroughput), labelled by direction either side of it.
                Labeler = value => value switch
                {
                    > 0 => $"↓ {ByteFormat.Rate(value)}",
                    < 0 => $"↑ {ByteFormat.Rate(-value)}",
                    _ => "0"
                },
                MinLimit = -MinimumScale,
                MaxLimit = MinimumScale,
                // Five labels whatever the scale: both limits, halfway each way, and zero.
                MinStep = MinimumScale / 2,
                ForceStepToMin = true
            }
        ];

        ApplyTheme(theme.Chart);
        theme.ThemeChanged += () => ApplyTheme(theme.Chart);

        broker.ThroughputUpdated += OnThroughput;
        broker.UsageUpdated += OnUsage;
        broker.ConnectionsUpdated += OnConnections;

        if (broker.LatestNetwork is { } network) OnNetwork(network);
        OnHealth(broker.LatestHealth);
        if (budgets.Latest is { } status) OnBudget(status);
        broker.NetworkChanged += OnNetwork;
        broker.HealthUpdated += OnHealth;
        budgets.BudgetUpdated += OnBudget;
    }

    [RelayCommand]
    private void OpenNetwork() => _navigation.NavigateTo<NetworkViewModel>();

    private void OnNetwork(NetworkSnapshot s)
    {
        ConnectionName = NetworkDisplay.ConnectionName(s);
        ConnectionGlyph = NetworkDisplay.MediumGlyph(s.Medium);

        var parts = new List<string> { NetworkDisplay.MediumName(s.Medium) };
        if (s.LinkSpeedBitsPerSecond is not null) parts.Add(NetworkDisplay.LinkSpeed(s.LinkSpeedBitsPerSecond));
        if (s.SignalPercent is { } signal) parts.Add($"signal {signal}%");
        if (s.IsMetered) parts.Add("metered");
        ConnectionDetail = string.Join(" · ", parts);

        RoutesText = s.Routes.Count == 0 ? null : $"Through {string.Join(", ", s.Routes.Select(r => r.Name))}";
    }

    private void OnHealth(HealthSample? sample)
    {
        bool enabled = _monitoring.HealthChecksEnabled;
        HealthStatus = NetworkDisplay.Status(sample, enabled);
        HealthText = !enabled ? "Quality checks off"
            : sample?.InternetLatencyMs is { } ms ? $"{NetworkDisplay.StatusName(HealthStatus)} · {ms:0} ms"
            : NetworkDisplay.StatusName(HealthStatus);
        HealthMeaning = NetworkDisplay.StatusMeaning(sample, enabled, _healthOptions);
    }

    private void OnBudget(BudgetStatus status)
    {
        TodayBytesReceived = status.TodayBytesReceived;
        TodayBytesSent = status.TodayBytesSent;

        DailyBudgetFraction = status.DailyFraction;
        DailyBudgetText = status.DailyBudgetBytes is { } daily
            ? $"{status.DailyFraction:P0} of {ByteFormat.Size(daily)} daily budget"
            : null;

        MonthlyBudgetFraction = status.MonthlyFraction;
        MonthlyBudgetText = status.MonthlyBudgetBytes is { } monthly
            ? $"{ByteFormat.Size(status.PeriodBytes)} of {ByteFormat.Size(monthly)} since {status.PeriodStart:d MMM}"
            : null;
    }

    /// <summary>
    /// Paints are replaced on the existing series and axes rather than rebuilding them, so the
    /// five minutes of throughput history on screen survive a theme switch.
    /// </summary>
    private void ApplyTheme(ChartTheme theme)
    {
        _download.Stroke = ChartPaints.Stroke(theme.Download);
        _download.Fill = ChartPaints.Fill(theme.Download, 48);
        _upload.Stroke = ChartPaints.Stroke(theme.Upload);
        _upload.Fill = ChartPaints.Fill(theme.Upload, 48);

        ThroughputXAxes[0].LabelsPaint = ChartPaints.Labels(theme);
        ThroughputYAxes[0].LabelsPaint = ChartPaints.Labels(theme);
        ThroughputYAxes[0].SeparatorsPaint = ChartPaints.Separators(theme);

        TooltipTextPaint = ChartPaints.Text(theme);
        TooltipBackgroundPaint = ChartPaints.TooltipBackground(theme);
    }

    private void OnThroughput(double down, double up)
    {
        DownloadBytesPerSecond = down;
        UploadBytesPerSecond = up;

        var now = DateTime.Now;
        DownloadPoints.Add(new DateTimePoint(now, down));
        UploadPoints.Add(new DateTimePoint(now, -up));

        while (DownloadPoints.Count > HistorySeconds) DownloadPoints.RemoveAt(0);
        while (UploadPoints.Count > HistorySeconds) UploadPoints.RemoveAt(0);

        // Always the last five minutes, ending now: the chart runs right to left from the first
        // second instead of stretching a few seconds of data across its whole width.
        var x = ThroughputXAxes[0];
        x.MinLimit = now.AddSeconds(-HistorySeconds).Ticks;
        x.MaxLimit = now.Ticks;

        // The same scale above and below the line, so zero sits in the middle and a burst either
        // way is drawn to the same size. Left to itself the axis fits only the data, and a quiet
        // download with an upload burst puts zero at the very top. The scale moves in doublings,
        // so it changes only when traffic crosses one, not with every second's peak.
        double peak = Math.Max(
            DownloadPoints.Max(p => p.Value ?? 0),
            UploadPoints.Max(p => -(p.Value ?? 0)));
        double limit = MinimumScale;
        while (limit < peak * 1.15) limit *= 2;

        var y = ThroughputYAxes[0];
        if (y.MaxLimit != limit)
        {
            y.MinLimit = -limit;
            y.MaxLimit = limit;
            y.MinStep = limit / 2;
        }
    }

    /// <summary>The smallest half-range of the throughput chart, so an idle link is not all noise.</summary>
    private const double MinimumScale = 16 * 1024;

    private void OnUsage(IReadOnlyList<UsageSample> samples)
    {
        foreach (var sample in samples)
        {
            var key = (sample.AttributionId, sample.Interface, sample.BucketStartUtc.ToUnixTimeSeconds());

            _seen.TryGetValue(key, out var previous);
            long sentDelta = sample.BytesSent - previous.Sent;
            long receivedDelta = sample.BytesReceived - previous.Received;
            _seen[key] = (sample.BytesSent, sample.BytesReceived);

            if (!_appIndex.TryGetValue(sample.AttributionId, out var row))
            {
                row = new LiveAppRowViewModel(sample.AttributionId, sample.DisplayName);
                _appIndex[sample.AttributionId] = row;
                Apps.Add(row);
            }
            else if (!string.IsNullOrWhiteSpace(sample.DisplayName))
            {
                row.DisplayName = sample.DisplayName;
            }

            // Merge in place. Rebuilding the collection on every tick would throw away the user's
            // sort, their selection and their scroll position.
            row.Add(sentDelta, receivedDelta);
        }

        UpdateLastMinute(samples);
        TrimSeen();
    }

    /// <summary>
    /// "Last minute" is the last finished minute; the current one is still filling. Every read covers
    /// the last few minutes, so an app missing from that minute in this read used nothing in it and
    /// drops to zero, rather than keeping whatever rate it had when it was last seen.
    /// </summary>
    private void UpdateLastMinute(IReadOnlyList<UsageSample> samples)
    {
        long lastMinute = DateTimeOffset.UtcNow.FloorTo(Granularity.Minute).AddMinutes(-1).ToUnixTimeSeconds();

        var bytes = samples
            .Where(s => s.Granularity == Granularity.Minute && s.BucketStartUtc.ToUnixTimeSeconds() == lastMinute)
            .GroupBy(s => s.AttributionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Total), StringComparer.Ordinal);

        foreach (var row in Apps)
            row.RecentBytesPerSecond = bytes.GetValueOrDefault(row.AttributionId) / 60d;
    }

    private void TrimSeen()
    {
        if (_seen.Count < 20_000) return;

        long cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        foreach (var key in _seen.Keys.Where(k => k.Bucket < cutoff).ToList())
            _seen.Remove(key);
    }

    private void OnConnections(IReadOnlyList<ConnectionSnapshot> connections)
    {
        ActiveSocketCount = connections.Count;

        var grouped = connections
            .GroupBy(c => (c.ProcessName, c.Pid))
            .Select(g => new
            {
                g.Key.ProcessName,
                g.Key.Pid,
                Path = g.Select(c => c.ExecutablePath).FirstOrDefault(p => p is not null),
                Tcp = g.Count(c => c.Protocol == Protocols.Tcp),
                Udp = g.Count(c => c.Protocol == Protocols.Udp),
                Established = g.Count(c => c.State == TcpState.Established)
            })
            .OrderByDescending(x => x.Established)
            .ThenByDescending(x => x.Tcp + x.Udp)
            .Take(40)
            .ToList();

        ProcessCount = grouped.Count;

        // Small, bounded list — a straight rebuild is cheaper here than diffing, and there is no
        // user sort state on this table to protect.
        Sockets.Clear();
        foreach (var item in grouped)
        {
            Sockets.Add(new SocketRowViewModel(item.ProcessName, item.Pid, _icons.Get(item.Path))
            {
                TcpCount = item.Tcp,
                UdpCount = item.Udp,
                EstablishedCount = item.Established
            });
        }
    }

    private static string SafeTicksToTime(double value)
    {
        if (value < DateTime.MinValue.Ticks || value > DateTime.MaxValue.Ticks) return string.Empty;
        return new DateTime((long)value).ToString("HH:mm:ss");
    }
}
