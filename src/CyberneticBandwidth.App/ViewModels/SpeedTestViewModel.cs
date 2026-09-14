using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.ViewModels;

/// <summary>How a figure moved against the previous test: +1 better, -1 worse, 0 about the same.</summary>
public sealed record ChangeInfo(string Text, int Direction)
{
    public bool IsBetter => Direction > 0;
    public bool IsWorse => Direction < 0;

    /// <summary>
    /// Differences under 3% (or a hair's width in absolute terms) read as "same": two back-to-back
    /// tests never match exactly, and an arrow on noise would suggest a change that did not happen.
    /// </summary>
    public static ChangeInfo? Between(double? current, double? previous, bool higherIsBetter, Func<double, string> format)
    {
        if (current is not { } now || previous is not { } before) return null;

        double diff = now - before;
        if (Math.Abs(diff) < Math.Max(Math.Abs(before) * 0.03, 0.05)) return new("≈ same", 0);

        int direction = (diff > 0) == higherIsBetter ? 1 : -1;
        return new($"{(diff > 0 ? "▲" : "▼")} {format(Math.Abs(diff))}", direction);
    }
}

/// <summary>One everyday activity and whether a result supports it; Change is set when it flipped since the previous test.</summary>
public sealed record SuitabilityRow(string Activity, bool IsOk, string Detail, string? Change = null);

public sealed record SpeedTestRow(string When, string Network, string Download, string Upload, string Latency, bool IsCompared);

public sealed record RegionHistoryRow(string When, string City, string Latency, string Download, string Upload, string Note, bool HasError);

public enum SpeedTestTab
{
    Local,
    World
}

/// <summary>One region on the "Around the world" list: its latest result, or its progress while testing.</summary>
public sealed partial class RegionRow(SpeedTestRegion region) : ObservableObject
{
    public SpeedTestRegion Region { get; } = region;
    public string City => Region.City;
    public string Place => $"{Region.Country} · {Region.Continent}";

    /// <summary>Where upload goes, or why it is not measured; the upload figure's tooltip.</summary>
    public string UploadNote => Region.UploadCity is { } city ? $"Upload is measured to a server in {city}."
        : Region.MeasuresUpload ? $"Upload is measured to a server in {Region.City}."
        : "Upload is not measured here: there is no public test server in this city that accepts uploads.";

    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _download = "—";
    [ObservableProperty] private string _upload = "—";
    [ObservableProperty] private double _downloadFraction;
    [ObservableProperty] private string _status = "Not tested yet";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private ChangeInfo? _latencyChange;
    [ObservableProperty] private ChangeInfo? _downloadChange;
    [ObservableProperty] private ChangeInfo? _uploadChange;

    public RegionalSpeedResult? Latest { get; set; }
    public RegionalSpeedResult? Previous { get; set; }
}

/// <summary>
/// The Speed test page, in two tabs that each carry their own verdicts, chart, table and comparison:
///  - <b>Local</b>: Cloudflare's nearest data centre, download, upload, response time and jitter.
///  - <b>Around the world</b>: fixed servers in ten cities, response time and download per region,
///    and upload where the region has an upload server.
/// </summary>
public sealed partial class SpeedTestViewModel : ObservableObject
{
    private const int RegionalHistoryRows = 300;

    private readonly SpeedTestService _speedTest;
    private readonly LiveActivityBroker _broker;
    private readonly IServiceScopeFactory _scopes;
    private readonly ThemeService _theme;
    private readonly ILogger<SpeedTestViewModel> _log;
    private IReadOnlyList<SpeedTestResult> _history = [];
    private SpeedTestResult? _shown;
    private CancellationTokenSource? _cancel;
    private bool _meteredConfirmed;
    private bool _regionsMeteredConfirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalTab), nameof(IsWorldTab))]
    private SpeedTestTab _selectedTab = SpeedTestTab.Local;

    public bool IsLocalTab
    {
        get => SelectedTab == SpeedTestTab.Local;
        set { if (value) SelectedTab = SpeedTestTab.Local; }
    }

    public bool IsWorldTab
    {
        get => SelectedTab == SpeedTestTab.World;
        set { if (value) SelectedTab = SpeedTestTab.World; }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(TestAllRegionsCommand), nameof(TestRegionCommand))]
    private bool _isRunning;

    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = null!;
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = null!;
    [ObservableProperty] private string? _routeNote;

    // ---------------------------------------------------------------- local tab

    [ObservableProperty] private bool _isNearestRunning;
    [ObservableProperty] private string _phaseText = "Measures download, upload and response time against Cloudflare's nearest server. Takes about 25 seconds.";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _startLabel = "Start test";
    [ObservableProperty] private string? _warning;

    [ObservableProperty] private string _download = "—";
    [ObservableProperty] private string _upload = "—";
    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _resultSummary = string.Empty;

    [ObservableProperty] private bool _compareLocal;
    [ObservableProperty] private string? _localCompareNote;
    [ObservableProperty] private ChangeInfo? _downloadChange;
    [ObservableProperty] private ChangeInfo? _uploadChange;
    [ObservableProperty] private ChangeInfo? _latencyChange;
    [ObservableProperty] private ChangeInfo? _jitterChange;

    [ObservableProperty] private ISeries[] _historySeries = [];
    [ObservableProperty] private Axis[] _historyXAxes = [];
    [ObservableProperty] private Axis[] _historyYAxes = [];

    public ObservableCollection<SuitabilityRow> Suitability { get; } = [];
    public ObservableCollection<SpeedTestRow> History { get; } = [];

    // ---------------------------------------------------------------- around the world tab

    [ObservableProperty] private bool _isRegionalRunning;
    [ObservableProperty] private string _regionsStartLabel = "Test all regions";
    [ObservableProperty] private string? _regionsWarning;
    [ObservableProperty] private string _regionsSummary = "Not tested yet.";

    [ObservableProperty] private bool _compareRegions;
    [ObservableProperty] private string? _regionsCompareNote;

    [ObservableProperty] private ISeries[] _regionSeries = [];
    [ObservableProperty] private Axis[] _regionXAxes = [];
    [ObservableProperty] private Axis[] _regionYAxes = [];

    public ObservableCollection<RegionRow> Regions { get; } =
        new(SpeedTestService.Regions.Select(r => new RegionRow(r)));

    public ObservableCollection<SuitabilityRow> RegionSuitability { get; } = [];
    public ObservableCollection<RegionHistoryRow> RegionHistory { get; } = [];

    public SpeedTestViewModel(
        SpeedTestService speedTest,
        LiveActivityBroker broker,
        IServiceScopeFactory scopes,
        ThemeService theme,
        ILogger<SpeedTestViewModel> log)
    {
        _speedTest = speedTest;
        _broker = broker;
        _scopes = scopes;
        _theme = theme;
        _log = log;

        ApplyChrome();
        theme.ThemeChanged += () =>
        {
            ApplyChrome();
            BuildHistoryChart();
            BuildRegionChart();
        };
        broker.NetworkChanged += _ => UpdateNotes();
        UpdateNotes();
    }

    partial void OnCompareLocalChanged(bool value)
    {
        RefreshLocalComparison();
        FillHistoryTable();
    }

    partial void OnCompareRegionsChanged(bool value) => RefreshRegionalComparison();

    [RelayCommand]
    public async Task ReloadAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>();

            _history = await repo.GetSpeedTestsAsync(50, CancellationToken.None);
            if (!IsNearestRunning && _history.Count > 0 && (_shown is null || _shown.ObservedUtc < _history[0].ObservedUtc))
                ShowResult(_history[0], isNew: false);
            FillHistoryTable();
            BuildHistoryChart();

            if (!IsRegionalRunning) await LoadRegionalAsync(repo);
            UpdateNotes();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load speed test history.");
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancel?.Cancel();

    // ================================================================ local test

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // A test moves tens to hundreds of megabytes; on a metered link, ask once before spending them.
        if (_broker.LatestNetwork is { IsMetered: true } && !_meteredConfirmed)
        {
            _meteredConfirmed = true;
            StartLabel = "Run anyway";
            Warning = "This connection is metered. A test typically uses 50 to 300 MB, depending on its speed.";
            return;
        }

        IsRunning = true;
        IsNearestRunning = true;
        StartLabel = "Testing…";
        Warning = null;
        Download = Upload = Latency = Jitter = "—";
        DownloadChange = UploadChange = LatencyChange = JitterChange = null;
        LocalCompareNote = null;
        Suitability.Clear();
        ResultSummary = string.Empty;
        _cancel = new CancellationTokenSource();

        var progress = new Progress<SpeedTestProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => _speedTest.RunAsync(progress, _cancel.Token));
            await ReloadAsync();
            ShowResult(result, isNew: true);
        }
        catch (OperationCanceledException)
        {
            PhaseText = "Test cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speed test failed.");
            PhaseText = $"The test could not finish: {ex.GetBaseException().Message}";
        }
        finally
        {
            IsRunning = false;
            IsNearestRunning = false;
            Progress = 0;
            StartLabel = "Test again";
            _meteredConfirmed = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private void OnProgress(SpeedTestProgress p)
    {
        Progress = p.Fraction;

        switch (p.Phase)
        {
            case SpeedTestPhase.Latency:
                PhaseText = "Measuring response time…";
                Latency = NetworkDisplay.Milliseconds(p.LatencyMs);
                break;
            case SpeedTestPhase.Download:
                PhaseText = "Measuring download speed…";
                Latency = NetworkDisplay.Milliseconds(p.LatencyMs);
                if (p.CurrentMbps is { } down) Download = Mbps(down);
                break;
            case SpeedTestPhase.Upload:
                PhaseText = "Measuring upload speed…";
                if (p.CurrentMbps is { } up) Upload = Mbps(up);
                break;
        }
    }

    private void ShowResult(SpeedTestResult r, bool isNew)
    {
        _shown = r;
        Download = Mbps(r.DownloadMbps);
        Upload = Mbps(r.UploadMbps);
        Latency = NetworkDisplay.Milliseconds(r.LatencyMs);
        Jitter = NetworkDisplay.Milliseconds(r.JitterMs);

        PhaseText = isNew ? "Done." : $"Last test: {r.ObservedUtc.LocalDateTime:dddd d MMMM, HH:mm}.";
        ResultSummary = $"{r.Server}" +
                        (r.NetworkName is null ? string.Empty : $" · on {r.NetworkName}") +
                        (r.PublicAddress is null ? string.Empty : $" · seen online as {r.PublicAddress}") +
                        $" · used {ByteFormat.Size(r.BytesUsed)}";

        RefreshLocalComparison();
    }

    /// <summary>The test before the one on screen, which is what "previous" means for the Local tab.</summary>
    private SpeedTestResult? PreviousLocal()
    {
        if (_shown is null) return null;
        return _history.Where(h => h.ObservedUtc < _shown.ObservedUtc).MaxBy(h => h.ObservedUtc);
    }

    private void RefreshLocalComparison()
    {
        Suitability.Clear();
        if (_shown is not { } current) return;

        var previous = CompareLocal ? PreviousLocal() : null;

        DownloadChange = previous is null ? null : ChangeInfo.Between(current.DownloadMbps, previous.DownloadMbps, true, Mbps);
        UploadChange = previous is null ? null : ChangeInfo.Between(current.UploadMbps, previous.UploadMbps, true, Mbps);
        LatencyChange = previous is null ? null : ChangeInfo.Between(current.LatencyMs, previous.LatencyMs, false, v => $"{v:0} ms");
        JitterChange = previous is null ? null : ChangeInfo.Between(current.JitterMs, previous.JitterMs, false, v => $"{v:0} ms");

        LocalCompareNote = !CompareLocal ? null
            : previous is null ? "There is no earlier test to compare with yet."
            : $"Compared with the test on {NetworkDisplay.When(previous.ObservedUtc)}" +
              (previous.NetworkName is { } net && net != current.NetworkName ? $" (on {net})." : ".");

        var before = previous is null ? null : Assess(previous).ToDictionary(r => r.Activity);
        foreach (var row in Assess(current))
            Suitability.Add(row with { Change = Flip(row, before) });
    }

    /// <summary>A note only when the verdict changed; an unchanged verdict needs no comment.</summary>
    private static string? Flip(SuitabilityRow now, IReadOnlyDictionary<string, SuitabilityRow>? before)
    {
        if (before is null || !before.TryGetValue(now.Activity, out var was) || was.IsOk == now.IsOk) return null;
        return now.IsOk ? "Better than last time, when it fell short." : "Worse than last time, when it was fine.";
    }

    /// <summary>
    /// Plain-language verdicts, using the commonly published requirements: video-call apps ask
    /// for roughly 4 Mbps each way for HD group calls, streaming services 25 Mbps for 4K, and
    /// real-time games stay playable under about 60 ms with steady timing.
    /// </summary>
    private static IEnumerable<SuitabilityRow> Assess(SpeedTestResult r)
    {
        yield return new("Browsing and email", r.DownloadMbps >= 2 && r.LatencyMs < 300,
            r.DownloadMbps >= 2 ? "Plenty for everyday use." : "Pages will be slow to load.");

        yield return new("HD video calls", r.DownloadMbps >= 4 && r.UploadMbps >= 4 && r.LatencyMs < 150 && r.JitterMs < 30,
            r.DownloadMbps < 4 ? $"Download of {Mbps(r.DownloadMbps)} may blur other people's video; about 4 Mbps is needed."
            : r.UploadMbps < 4 ? $"Upload of {Mbps(r.UploadMbps)} may blur your video; about 4 Mbps is needed."
            : r.LatencyMs >= 150 || r.JitterMs >= 30 ? "Delay or unsteady timing may make people talk over each other."
            : "Teams, Zoom and Meet should run smoothly.");

        yield return new("4K streaming", r.DownloadMbps >= 25,
            r.DownloadMbps >= 25 ? "Enough for one 4K stream." : $"{Mbps(r.DownloadMbps)} suits HD rather than 4K (25 Mbps).");

        yield return new("Online gaming", r.LatencyMs < 60 && r.JitterMs < 20,
            r.LatencyMs < 60 && r.JitterMs < 20 ? "Response time is quick and steady." : $"{r.LatencyMs:0} ms response time may feel laggy in fast games.");

        yield return new("Large uploads and cloud backup", r.UploadMbps >= 20,
            $"A 1 GB file takes about {UploadMinutes(r.UploadMbps)} to upload.");

        yield return new("Several people streaming at once", r.DownloadMbps >= 50,
            r.DownloadMbps >= 50 ? "Room for a busy household or small office." : "Streams may compete when several run together.");
    }

    private static string UploadMinutes(double mbps)
    {
        if (mbps <= 0) return "a very long time";
        double minutes = 8_000 / mbps / 60;
        return minutes < 1 ? "under a minute" : $"{minutes:0} minutes";
    }

    private void FillHistoryTable()
    {
        var previous = CompareLocal ? PreviousLocal() : null;
        History.Clear();
        foreach (var r in _history)
        {
            bool compared = CompareLocal && (r == _shown || r == previous);
            History.Add(new SpeedTestRow(NetworkDisplay.When(r.ObservedUtc), r.NetworkName ?? "—",
                Mbps(r.DownloadMbps), Mbps(r.UploadMbps), NetworkDisplay.Milliseconds(r.LatencyMs), compared));
        }
    }

    private void BuildHistoryChart()
    {
        var theme = _theme.Chart;
        var ordered = _history.Reverse().ToList();

        HistorySeries =
        [
            new LineSeries<ObservablePoint>
            {
                Name = "Download",
                Values = ordered.Select((r, i) => new ObservablePoint(i, r.DownloadMbps)).ToArray(),
                GeometrySize = 6, LineSmoothness = 0, Fill = null,
                Stroke = ChartPaints.Stroke(theme.Download, 2), GeometryStroke = ChartPaints.Stroke(theme.Download, 2),
                GeometryFill = ChartPaints.Fill(theme.Surface),
                YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
            },
            new LineSeries<ObservablePoint>
            {
                Name = "Upload",
                Values = ordered.Select((r, i) => new ObservablePoint(i, r.UploadMbps)).ToArray(),
                GeometrySize = 6, LineSmoothness = 0, Fill = null,
                Stroke = ChartPaints.Stroke(theme.Upload, 2), GeometryStroke = ChartPaints.Stroke(theme.Upload, 2),
                GeometryFill = ChartPaints.Fill(theme.Surface),
                YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
            }
        ];

        HistoryXAxes =
        [
            new Axis
            {
                MinStep = 1,
                Labeler = x =>
                {
                    int i = (int)Math.Round(x);
                    return i >= 0 && i < ordered.Count ? ordered[i].ObservedUtc.LocalDateTime.ToString("d MMM HH:mm") : string.Empty;
                },
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
        HistoryYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} Mbps",
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = ChartPaints.Separators(theme)
            }
        ];
    }

    // ================================================================ around the world

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task TestAllRegionsAsync() => RunRegionsAsync(Regions.ToList());

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task TestRegionAsync(RegionRow? row) => row is null ? Task.CompletedTask : RunRegionsAsync([row]);

    private async Task RunRegionsAsync(IReadOnlyList<RegionRow> rows)
    {
        if (_broker.LatestNetwork is { IsMetered: true } && !_regionsMeteredConfirmed)
        {
            _regionsMeteredConfirmed = true;
            RegionsStartLabel = "Run anyway";
            RegionsWarning = "This connection is metered. Each region downloads for about 6 seconds and, where it can, uploads for about 6 more, typically 5 to 100 MB.";
            return;
        }

        IsRunning = true;
        IsRegionalRunning = true;
        RegionsStartLabel = "Testing…";
        RegionsWarning = null;
        foreach (var row in rows)
        {
            row.IsTesting = true;
            row.Status = "Waiting…";
            row.LatencyChange = row.DownloadChange = row.UploadChange = null;
        }
        _cancel = new CancellationTokenSource();

        var progress = new Progress<RegionalProgress>(OnRegionalProgress);

        try
        {
            await Task.Run(() => _speedTest.RunRegionsAsync(rows.Select(r => r.Region).ToList(), progress, _cancel.Token));
        }
        catch (OperationCanceledException)
        {
            RegionsWarning = "Test cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Regional speed test failed.");
            RegionsWarning = $"The test could not finish: {ex.GetBaseException().Message}";
        }
        finally
        {
            IsRunning = false;
            IsRegionalRunning = false;
            RegionsStartLabel = "Test all regions";
            _regionsMeteredConfirmed = false;
            _cancel?.Dispose();
            _cancel = null;

            // Everything the run stored, and regions it never reached back to their last result.
            await using var scope = _scopes.CreateAsyncScope();
            await LoadRegionalAsync(scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>());
        }
    }

    private void OnRegionalProgress(RegionalProgress p)
    {
        var row = Regions.FirstOrDefault(r => r.Region.Code == p.RegionCode);
        if (row is null) return;

        switch (p.Phase)
        {
            case SpeedTestPhase.Latency:
                row.Status = "Measuring response time…";
                if (p.LatencyMs is { } ms) row.Latency = NetworkDisplay.Milliseconds(ms);
                break;
            case SpeedTestPhase.Download:
                row.Status = "Measuring download…";
                if (p.CurrentMbps is { } mbps) row.Download = Mbps(mbps);
                break;
            case SpeedTestPhase.Upload:
                row.Status = row.Region.UploadCity is { } city ? $"Measuring upload to {city}…" : "Measuring upload…";
                if (p.CurrentMbps is { } up) row.Upload = Mbps(up);
                break;
            case SpeedTestPhase.Done:
                row.IsTesting = false;
                break;
        }
    }

    private async Task LoadRegionalAsync(INetworkHistoryRepository repo)
    {
        try
        {
            // Results saved before a stalled download counted as "not measured" carry 0 Mbps.
            var history = (await repo.GetRegionalSpeedTestsAsync(RegionalHistoryRows, CancellationToken.None))
                .Select(h => h.DownloadMbps is > 0 ? h : h with { DownloadMbps = null })
                .ToList();

            foreach (var row in Regions)
            {
                var mine = history.Where(h => h.RegionCode == row.Region.Code).Take(2).ToList();
                row.IsTesting = false;
                row.Latest = mine.ElementAtOrDefault(0);
                row.Previous = mine.ElementAtOrDefault(1);
                ShowRegion(row);
            }

            RegionHistory.Clear();
            foreach (var h in history)
            {
                var region = SpeedTestService.Regions.FirstOrDefault(r => r.Code == h.RegionCode);
                RegionHistory.Add(new RegionHistoryRow(
                    NetworkDisplay.When(h.ObservedUtc),
                    region?.City ?? h.RegionCode,
                    NetworkDisplay.Milliseconds(h.LatencyMs),
                    h.DownloadMbps is { } mbps ? Mbps(mbps) : "—",
                    h.UploadMbps is { } up ? Mbps(up) : "—",
                    h.Error ?? h.NetworkName ?? string.Empty,
                    h.Error is not null));
            }

            RefreshRegionalComparison();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load regional speed test results.");
        }
    }

    private static void ShowRegion(RegionRow row)
    {
        if (row.Latest is not { } result)
        {
            row.Latency = row.Download = row.Upload = "—";
            row.Status = "Not tested yet";
            row.HasError = false;
            return;
        }

        row.Latency = NetworkDisplay.Milliseconds(result.LatencyMs);
        row.Download = result.DownloadMbps is { } mbps ? Mbps(mbps) : "—";
        row.Upload = result.UploadMbps is { } up ? Mbps(up) : "—";
        row.HasError = result.Error is not null;
        row.Status = result.Error is { } error
            ? $"{error} · {NetworkDisplay.When(result.ObservedUtc)}"
            : $"Tested {NetworkDisplay.When(result.ObservedUtc)}";
    }

    private void RefreshRegionalComparison()
    {
        double fastest = Regions.Max(r => r.Latest?.DownloadMbps ?? 0);
        foreach (var row in Regions)
        {
            row.DownloadFraction = fastest > 0 && row.Latest?.DownloadMbps is { } mbps ? mbps / fastest : 0;

            bool compare = CompareRegions && row.Latest is not null && row.Previous is not null;
            row.LatencyChange = compare ? ChangeInfo.Between(row.Latest!.LatencyMs, row.Previous!.LatencyMs, false, v => $"{v:0} ms") : null;
            row.DownloadChange = compare ? ChangeInfo.Between(row.Latest!.DownloadMbps, row.Previous!.DownloadMbps, true, Mbps) : null;
            row.UploadChange = compare ? ChangeInfo.Between(row.Latest!.UploadMbps, row.Previous!.UploadMbps, true, Mbps) : null;
        }

        var measured = Regions.Where(r => r.Latest?.LatencyMs is not null).ToList();
        if (measured.Count == 0)
        {
            RegionsSummary = "Not tested yet.";
        }
        else
        {
            var closest = measured.MinBy(r => r.Latest!.LatencyMs)!;
            var furthest = measured.MaxBy(r => r.Latest!.LatencyMs)!;
            RegionsSummary = measured.Count == 1
                ? $"{closest.City} answers in {closest.Latency}."
                : $"Quickest to answer: {closest.City} ({closest.Latency}). Slowest: {furthest.City} ({furthest.Latency}).";
        }

        int comparable = Regions.Count(r => r.Latest is not null && r.Previous is not null);
        RegionsCompareNote = !CompareRegions ? null
            : comparable == 0 ? "No region has been tested twice yet, so there is nothing to compare."
            : $"Each region against its own previous result ({comparable} of {Regions.Count} have one).";

        RegionSuitability.Clear();
        var now = AssessContinents(r => r.Latest).ToList();
        var before = CompareRegions ? AssessContinents(r => r.Previous).ToDictionary(r => r.Activity) : null;
        foreach (var row in now) RegionSuitability.Add(row with { Change = Flip(row, before) });

        BuildRegionChart();
    }

    /// <summary>
    /// One verdict per continent, from its quickest-answering server: the one services there would
    /// most likely be reached through. Same thresholds as the Local tab's verdicts.
    /// </summary>
    private IEnumerable<SuitabilityRow> AssessContinents(Func<RegionRow, RegionalSpeedResult?> pick)
    {
        foreach (var continent in Regions.GroupBy(r => r.Region.Continent))
        {
            var best = continent
                .Select(r => (Row: r, Result: pick(r)))
                .Where(x => x.Result?.LatencyMs is not null)
                .MinBy(x => x.Result!.LatencyMs);
            if (best.Result is not { LatencyMs: { } ms } result) continue;

            double mbps = result.DownloadMbps ?? 0;
            double jitter = result.JitterMs ?? 0;

            // Upload only counts against calls where it was measured; most regions have no upload server.
            bool uploadHolds = result.UploadMbps is not { } up || up >= 4;
            string calls = ms < 150 && jitter < 30 && mbps >= 4 && uploadHolds ? "video calls feel natural"
                : ms < 150 && jitter < 30 && mbps >= 4 ? "calls connect quickly, but your own video may blur"
                : ms < 300 && mbps >= 2 ? "calls work with a noticeable delay"
                : "calls will lag and talk over each other";
            string downloads = mbps >= 25 ? "downloads and 4K from services hosted there are fast"
                : mbps >= 5 ? "HD streaming and everyday downloads are fine"
                : result.DownloadMbps is null ? "download speed could not be measured"
                : "downloads from there are slow";
            string gaming = ms < 60 ? "quick enough for fast games" : ms < 120 ? "fine for casual games" : "too far away for fast-paced games";

            var figures = new List<string> { $"{ms:0} ms", result.DownloadMbps is { } d ? $"{Mbps(d)} down" : "no download" };
            if (result.UploadMbps is { } u) figures.Add($"{Mbps(u)} up");

            yield return new SuitabilityRow(
                $"{continent.Key} · via {best.Row.City}",
                ms < 150 && mbps >= 5,
                $"{Capitalise(calls)}; {downloads}; {gaming}. ({string.Join(", ", figures)})");
        }
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Download and upload bars per region against the left axis and response time as dots against the
    /// right, the dots in the text colour so the upload colour means upload alone. Comparing adds the
    /// previous results as faded bars and hollow dots beside them.
    /// </summary>
    private void BuildRegionChart()
    {
        var theme = _theme.Chart;
        var rows = Regions.ToList();

        ObservablePoint[] Points(Func<RegionRow, double?> value) =>
            rows.Select((r, i) => new ObservablePoint(i, value(r))).ToArray();

        // Previous results go in first: their bars sit left of today's (before, then now) and today's
        // filled dots draw over the hollow ones when a region barely changed.
        var series = new List<ISeries>();

        if (CompareRegions)
        {
            series.Add(new ColumnSeries<ObservablePoint>
            {
                Name = "Download (previous)",
                Values = Points(r => r.Previous?.DownloadMbps),
                Fill = ChartPaints.Fill(theme.Download, 80), Stroke = null, MaxBarWidth = 26, Rx = 2, Ry = 2,
                YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
            });
        }

        series.Add(new ColumnSeries<ObservablePoint>
        {
            Name = "Download",
            Values = Points(r => r.Latest?.DownloadMbps),
            Fill = ChartPaints.Fill(theme.Download), Stroke = null, MaxBarWidth = 26, Rx = 2, Ry = 2,
            YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
        });

        if (CompareRegions)
        {
            series.Add(new ColumnSeries<ObservablePoint>
            {
                Name = "Upload (previous)",
                Values = Points(r => r.Previous?.UploadMbps),
                Fill = ChartPaints.Fill(theme.Upload, 80), Stroke = null, MaxBarWidth = 26, Rx = 2, Ry = 2,
                YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
            });
        }

        series.Add(new ColumnSeries<ObservablePoint>
        {
            Name = "Upload",
            Values = Points(r => r.Latest?.UploadMbps),
            Fill = ChartPaints.Fill(theme.Upload), Stroke = null, MaxBarWidth = 26, Rx = 2, Ry = 2,
            YToolTipLabelFormatter = p => Mbps(p.Coordinate.PrimaryValue)
        });

        if (CompareRegions)
        {
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = "Response time (previous)",
                Values = Points(r => r.Previous?.LatencyMs),
                ScalesYAt = 1,
                Stroke = null, Fill = null, GeometrySize = 10,
                GeometryFill = ChartPaints.Fill(theme.Surface), GeometryStroke = ChartPaints.Stroke(theme.Text, 1.5f),
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:0} ms"
            });
        }

        series.Add(new LineSeries<ObservablePoint>
        {
            Name = "Response time",
            Values = Points(r => r.Latest?.LatencyMs),
            ScalesYAt = 1,
            Stroke = null, Fill = null, GeometrySize = 10,
            GeometryFill = ChartPaints.Fill(theme.Text), GeometryStroke = ChartPaints.Stroke(theme.Text, 2),
            YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:0} ms"
        });

        RegionSeries = [.. series];

        RegionXAxes =
        [
            new Axis
            {
                // Every city gets its label; the axis would otherwise skip alternate ones to avoid crowding.
                MinStep = 1,
                ForceStepToMin = true,
                TextSize = 13,
                Labeler = x =>
                {
                    int i = (int)Math.Round(x);
                    return i >= 0 && i < rows.Count ? rows[i].City : string.Empty;
                },
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];

        RegionYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} Mbps",
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = ChartPaints.Separators(theme)
            },
            new Axis
            {
                MinLimit = 0,
                Position = AxisPosition.End,
                Labeler = v => $"{v:0} ms",
                LabelsPaint = ChartPaints.Labels(theme),
                SeparatorsPaint = null
            }
        ];
    }

    // ================================================================ shared

    private void UpdateNotes()
    {
        var routes = _broker.LatestNetwork?.Routes ?? [];
        RouteNote = routes.Count == 0
            ? null
            : $"Measured through {string.Join(" and ", routes.Select(r => $"{r.Name} ({r.KindName})"))}, so results describe that path rather than your line alone.";
    }

    private void ApplyChrome()
    {
        TooltipTextPaint = ChartPaints.Text(_theme.Chart);
        TooltipBackgroundPaint = ChartPaints.TooltipBackground(_theme.Chart);
    }

    private static string Mbps(double value) => value switch
    {
        >= 100 => $"{value:0} Mbps",
        > 0 and < 0.1 => "<0.1 Mbps",
        _ => $"{value:0.#} Mbps"
    };
}
