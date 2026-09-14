using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.ViewModels;

public sealed record RouteRow(string Name, string Kind, string Explanation);

public sealed record NetworkEventRow(string When, string Title, string? Detail, string Glyph, bool IsProblem);

public sealed record HealthRangeOption(TimeSpan Span, string Label);

/// <summary>
/// The connection itself: what it is, how well it is working right now, how that has changed,
/// and what has happened to it. Everything live arrives through the broker; history comes from
/// the store when the page opens or the range changes.
/// </summary>
public sealed partial class NetworkViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LiveActivityBroker _broker;
    private readonly ThemeService _theme;
    private readonly MonitoringSettings _monitoring;
    private readonly HealthOptions _healthOptions;
    private readonly ILogger<NetworkViewModel> _log;
    private readonly NavigationRequests _navigation;
    private IReadOnlyList<HealthSample> _history = [];
    private bool _isShown;

    // Connection
    [ObservableProperty] private string _networkName = "Not connected";
    [ObservableProperty] private string _mediumGlyph = NetworkDisplay.MediumGlyph(ConnectionMedium.None);
    [ObservableProperty] private string _mediumName = string.Empty;
    [ObservableProperty] private string _linkSpeed = "—";
    [ObservableProperty] private string? _signal;
    [ObservableProperty] private string _reachability = string.Empty;
    [ObservableProperty] private string _costText = string.Empty;
    [ObservableProperty] private string _addresses = "—";
    [ObservableProperty] private string _gateways = "—";
    [ObservableProperty] private string _dnsServers = "—";
    [ObservableProperty] private string _adapter = "—";

    // Quality
    [ObservableProperty] private HealthStatus _healthStatus = HealthStatus.Unknown;
    [ObservableProperty] private string _statusName = NetworkDisplay.StatusName(HealthStatus.Unknown);
    [ObservableProperty] private string _statusMeaning = string.Empty;
    [ObservableProperty] private string _internetLatency = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _loss = "—";
    [ObservableProperty] private string _gatewayLatency = "—";
    [ObservableProperty] private string _lastChecked = string.Empty;

    // History
    [ObservableProperty] private HealthRangeOption _selectedRange;
    [ObservableProperty] private ISeries[] _historySeries = [];
    [ObservableProperty] private Axis[] _historyXAxes = [];
    [ObservableProperty] private Axis[] _historyYAxes = [];
    [ObservableProperty] private RectangularSection[] _historySections = [];
    [ObservableProperty] private string? _historyMessage;
    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = null!;
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = null!;
    [ObservableProperty] private string _problemSummary = string.Empty;

    public ObservableCollection<RouteRow> Routes { get; } = [];
    public ObservableCollection<NetworkEventRow> Events { get; } = [];

    public IReadOnlyList<HealthRangeOption> Ranges { get; } =
    [
        new(TimeSpan.FromHours(1), "Last hour"),
        new(TimeSpan.FromHours(24), "Last 24 hours"),
        new(TimeSpan.FromDays(7), "Last 7 days")
    ];

    public NetworkViewModel(
        LiveActivityBroker broker,
        IServiceScopeFactory scopes,
        ThemeService theme,
        MonitoringSettings monitoring,
        IOptions<HealthOptions> healthOptions,
        NavigationRequests navigation,
        ILogger<NetworkViewModel> log)
    {
        _scopes = scopes;
        _broker = broker;
        _theme = theme;
        _monitoring = monitoring;
        _healthOptions = healthOptions.Value;
        _navigation = navigation;
        _log = log;
        // An hour: the page is often opened soon after Cybernetic Bandwidth Monitor starts, when a day's axis would
        // squeeze the few checks so far into a sliver at its right edge.
        _selectedRange = Ranges[0];

        if (broker.LatestNetwork is { } network) OnNetwork(network);
        OnHealth(broker.LatestHealth);

        broker.NetworkChanged += OnNetwork;
        broker.HealthUpdated += sample =>
        {
            OnHealth(sample);
            // While another page is showing, the chart would be rebuilt for nobody; ReloadAsync
            // reads the history afresh when this page is opened again.
            if (_isShown) AppendToHistory(sample);
        };
        broker.NetworkEventRecorded += record => Events.Insert(0, ToRow(record));

        ApplyChrome();
        theme.ThemeChanged += () =>
        {
            ApplyChrome();
            BuildHistoryChart();
        };
    }

    partial void OnSelectedRangeChanged(HealthRangeOption value) => _ = ReloadAsync();

    /// <summary>Navigation moved to another page: stop following, and let the history go.</summary>
    public void Hide()
    {
        _isShown = false;
        _history = [];
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        _isShown = true;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to - SelectedRange.Span;

            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>();

            _history = await repo.GetHealthSamplesAsync(from, to, CancellationToken.None);
            BuildHistoryChart();

            var events = await repo.GetNetworkEventsAsync(to - TimeSpan.FromDays(7), to, CancellationToken.None);
            Events.Clear();
            foreach (var record in events.Reverse()) Events.Add(ToRow(record));

            OnHealth(_broker.LatestHealth);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load network history.");
            HistoryMessage = "Network history could not be read. The log file has the details.";
        }
    }

    [RelayCommand]
    private void OpenSpeedTest() => _navigation.NavigateTo<SpeedTestViewModel>();

    private void OnNetwork(NetworkSnapshot s)
    {
        NetworkName = NetworkDisplay.ConnectionName(s);
        MediumGlyph = NetworkDisplay.MediumGlyph(s.Medium);
        MediumName = NetworkDisplay.MediumName(s.Medium);
        LinkSpeed = NetworkDisplay.LinkSpeed(s.LinkSpeedBitsPerSecond);
        Signal = s.Medium == ConnectionMedium.WiFi ? NetworkDisplay.Signal(s.SignalPercent) : null;
        Reachability = NetworkDisplay.Reachability(s.Reachability);
        CostText = s.IsApproachingDataLimit
            ? "Metered, near its data limit"
            : s.IsMetered ? "Metered: Windows limits background data" : "Not metered";
        Addresses = s.Addresses.Count == 0 ? "—" : string.Join("\n", s.Addresses);
        Gateways = s.Gateways.Count == 0 ? "—" : string.Join("\n", s.Gateways);
        DnsServers = s.DnsServers.Count == 0 ? "—" : string.Join("\n", s.DnsServers);
        Adapter = s.AdapterName ?? "—";

        Routes.Clear();
        foreach (var route in s.Routes) Routes.Add(new RouteRow(route.Name, route.KindName, NetworkDisplay.RouteExplanation(route)));
    }

    private void OnHealth(HealthSample? sample)
    {
        HealthStatus = NetworkDisplay.Status(sample, _monitoring.HealthChecksEnabled);
        StatusName = _monitoring.HealthChecksEnabled ? NetworkDisplay.StatusName(HealthStatus) : "Off";
        StatusMeaning = NetworkDisplay.StatusMeaning(sample, _monitoring.HealthChecksEnabled, _healthOptions);
        InternetLatency = NetworkDisplay.Milliseconds(sample?.InternetLatencyMs);
        Jitter = NetworkDisplay.Milliseconds(sample?.InternetJitterMs);
        Loss = sample is null ? "—" : $"{sample.InternetLossPercent:0}%";
        GatewayLatency = sample?.GatewayLatencyMs is null ? "no reply" : NetworkDisplay.Milliseconds(sample.GatewayLatencyMs);
        LastChecked = sample is null ? string.Empty : $"Checked {sample.ObservedUtc.LocalDateTime:HH:mm:ss}";
    }

    private void AppendToHistory(HealthSample sample)
    {
        _history = [.. _history.Where(h => h.ObservedUtc >= DateTimeOffset.UtcNow - SelectedRange.Span), sample];
        BuildHistoryChart();
    }

    private static NetworkEventRow ToRow(NetworkEventRecord r)
    {
        var local = r.ObservedUtc.LocalDateTime;
        string when = local.Date == DateTime.Today ? local.ToString("HH:mm") : NetworkDisplay.When(r.ObservedUtc);
        return new NetworkEventRow(when, r.Title, r.Detail, NetworkDisplay.EventGlyph(r.Kind), r.IsProblem);
    }

    private void ApplyChrome()
    {
        TooltipTextPaint = ChartPaints.Text(_theme.Chart);
        TooltipBackgroundPaint = ChartPaints.TooltipBackground(_theme.Chart);
    }

    /// <summary>
    /// Latency to the internet and to the router over time, with the stretches where the internet
    /// did not answer shaded. Long ranges are thinned to a median per slot so a week stays a few
    /// hundred points; medians, because one slow request should not look like a slow hour.
    /// </summary>
    private void BuildHistoryChart()
    {
        var theme = _theme.Chart;
        var span = SelectedRange.Span;
        var start = DateTimeOffset.UtcNow - span;

        TimeSpan slot = span.TotalHours switch
        {
            <= 1 => TimeSpan.Zero,
            <= 24 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(15)
        };

        double HoursFromStart(DateTimeOffset t) => (t - start).TotalHours;

        IEnumerable<(double X, double? Internet, double? Gateway)> points = slot == TimeSpan.Zero
            ? _history.Select(h => (HoursFromStart(h.ObservedUtc), h.InternetLatencyMs, h.GatewayLatencyMs))
            : _history
                .GroupBy(h => (long)((h.ObservedUtc - start).Ticks / slot.Ticks))
                .OrderBy(g => g.Key)
                .Select(g => (
                    X: (g.Key * slot.Ticks + slot.Ticks / 2d) / TimeSpan.TicksPerHour,
                    Internet: RoundTrips.Median(g.Select(h => h.InternetLatencyMs).OfType<double>()),
                    Gateway: RoundTrips.Median(g.Select(h => h.GatewayLatencyMs).OfType<double>())));

        var list = points.ToList();

        HistorySeries =
        [
            new LineSeries<ObservablePoint>
            {
                Name = "Internet",
                Values = list.Select(p => new ObservablePoint(p.X, p.Internet)).ToArray(),
                GeometrySize = 0,
                LineSmoothness = 0,
                Fill = null,
                Stroke = ChartPaints.Stroke(theme.Download, 1.6f),
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:0} ms"
            },
            new LineSeries<ObservablePoint>
            {
                Name = "Router",
                Values = list.Select(p => new ObservablePoint(p.X, p.Gateway)).ToArray(),
                GeometrySize = 0,
                LineSmoothness = 0,
                Fill = null,
                Stroke = ChartPaints.Stroke(theme.Muted, 1.2f),
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:0} ms"
            }
        ];

        // Consecutive failed checks become one shaded band.
        var bands = new List<RectangularSection>();
        HealthSample? bandStart = null, previous = null;
        foreach (var h in _history.Append<HealthSample?>(null))
        {
            bool down = h is not null && h.Status.IsDown();
            if (down && bandStart is null) bandStart = h;
            if (!down && bandStart is not null)
            {
                bands.Add(new RectangularSection
                {
                    Xi = HoursFromStart(bandStart.ObservedUtc),
                    Xj = HoursFromStart((previous ?? bandStart).ObservedUtc) + _healthOptions.Interval.TotalHours,
                    Fill = ChartPaints.Fill(theme.Alert, 60)
                });
                bandStart = null;
            }
            previous = h;
        }
        HistorySections = [.. bands];

        string format = span.TotalHours <= 24 ? "HH:mm" : "ddd HH:mm";
        HistoryXAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = span.TotalHours,
                Labeler = x => x < 0 ? string.Empty : start.AddHours(x).ToLocalTime().ToString(format),
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
        HistoryYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} ms",
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = ChartPaints.Separators(theme)
            }
        ];

        int problems = _history.Count(h => h.Status.IsDown() || h.Status == HealthStatus.Slow);
        ProblemSummary = _history.Count == 0
            ? string.Empty
            : problems == 0
                ? $"{_history.Count} checks, all good"
                : $"{problems} of {_history.Count} checks were slow or failed";

        HistoryMessage = _history.Count == 0
            ? _monitoring.HealthChecksEnabled
                ? "No checks recorded in this range yet. One runs every 30 seconds."
                : "Connection quality checks are turned off in Settings."
            : null;
    }
}
