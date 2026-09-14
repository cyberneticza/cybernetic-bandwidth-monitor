using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Interop;
using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Services;

/// <summary>
/// The collector. Three independent cadences, because the three data sources move at wildly
/// different speeds: NIC counters change continuously, the socket table changes every few
/// seconds, and NDU only settles on minute boundaries.
/// </summary>
public sealed class NetworkSamplerService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INetworkUsageSource> sources,
    IConnectionSource connections,
    IThroughputSource throughput,
    LiveActivityBroker broker,
    AppNameResolver names,
    IOptions<SamplerOptions> options,
    ILogger<NetworkSamplerService> log) : BackgroundService
{
    private const string MinuteWatermark = "usage.minute";

    private readonly SamplerOptions _options = options.Value;
    private INetworkUsageSource? _source;
    private HashSet<ConnectionKey> _previousSockets = [];
    private Dictionary<ConnectionKey, ConnectionSnapshot> _previousDetail = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _source = await SelectSourceAsync(ct);

        if (_source is null)
        {
            broker.PublishStatus(
                "Per-app byte counts are unavailable on this machine. Connections and total " +
                "throughput are still being recorded.");
        }
        else
        {
            broker.PublishStatus(null);
            await BackfillAsync(ct);
            await RefreshAppNamesAsync(ct);
        }

        await Task.WhenAll(
            LoopAsync(() => _options.ThroughputInterval, SampleThroughputAsync, "throughput", ct),
            LoopAsync(() => _options.ConnectionInterval, SampleConnectionsAsync, "connections", ct),
            LoopAsync(() => _options.UsageInterval, SampleUsageAsync, "usage", ct));
    }

    private async Task<INetworkUsageSource?> SelectSourceAsync(CancellationToken ct)
    {
        foreach (var candidate in sources.OrderByDescending(s => s.Priority))
        {
            if (await candidate.IsAvailableAsync(ct))
            {
                log.LogInformation("Using {Source} for per-application byte counts.", candidate.Name);
                return candidate;
            }
            log.LogInformation("{Source} is not available here; trying the next one.", candidate.Name);
        }
        return null;
    }

    /// <summary>
    /// First run only: pull whatever history NDU still holds, at hour granularity so the call
    /// count stays sane (24 x BackfillDays rather than 1440 x BackfillDays).
    /// </summary>
    private async Task BackfillAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

        if (await repo.GetWatermarkAsync(MinuteWatermark, ct) is not null) return;

        var to = DateTimeOffset.UtcNow;
        var from = to - TimeSpan.FromDays(_options.BackfillDays);

        log.LogInformation("First run — backfilling {Days} days of history from {Source}.",
            _options.BackfillDays, _source!.Name);
        // It can take a few minutes, and live figures wait for it.
        broker.PublishStatus(
            $"First start: reading the last {_options.BackfillDays} days of history from Windows. " +
            "Live figures start as soon as that is done, usually within a few minutes.");

        try
        {
            var samples = await _source.GetUsageAsync(from, to, Granularity.Hour, ct);
            await repo.UpsertBucketsAsync(samples, ct);
            await repo.RunRollupAsync(ct);
            log.LogInformation("Backfill wrote {Count} hourly buckets.", samples.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Backfill did not complete. Live collection continues regardless.");
        }
        finally
        {
            broker.PublishStatus(null);
        }

        await repo.SetWatermarkAsync(MinuteWatermark, to - TimeSpan.FromHours(1), ct);
    }

    /// <summary>
    /// Every start: names are only written when an app is first seen, so history recorded before
    /// a naming improvement (or for an app since updated or uninstalled) would otherwise keep
    /// its old label for as long as retention holds it.
    /// </summary>
    private async Task RefreshAppNamesAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

            int renamed = await repo.RefreshAppNamesAsync(names.Resolve, ct);
            if (renamed > 0) log.LogInformation("Renamed {Count} applications from their attribution ids.", renamed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not refresh application names. New samples are still named as they arrive.");
        }
    }

    private Task SampleThroughputAsync(CancellationToken ct)
    {
        var (down, up) = throughput.SampleRate();
        broker.PublishThroughput(down, up);
        return Task.CompletedTask;
    }

    private async Task SampleConnectionsAsync(CancellationToken ct)
    {
        var current = connections.GetActiveConnections();
        broker.PublishConnections(current);

        var currentKeys = new HashSet<ConnectionKey>(current.Count);
        var currentDetail = new Dictionary<ConnectionKey, ConnectionSnapshot>(current.Count);
        foreach (var c in current)
        {
            currentKeys.Add(c.Key);
            currentDetail[c.Key] = c;
        }

        // Only transitions are persisted. Writing the whole table every five seconds would add
        // roughly half a million rows a day and buy nothing.
        var events = new List<ConnectionEventRecord>();
        var now = DateTimeOffset.UtcNow;

        foreach (var c in current)
        {
            if (!_previousSockets.Contains(c.Key))
                events.Add(ToRecord(c, now, ConnectionEventKind.Opened));
        }

        foreach (var key in _previousSockets)
        {
            if (!currentKeys.Contains(key) && _previousDetail.TryGetValue(key, out var gone))
                events.Add(ToRecord(gone, now, ConnectionEventKind.Closed));
        }

        _previousSockets = currentKeys;
        _previousDetail = currentDetail;

        if (events.Count == 0) return;

        broker.PublishConnectionEvents(events);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
        await repo.InsertConnectionEventsAsync(events, ct);
    }

    private async Task SampleUsageAsync(CancellationToken ct)
    {
        if (_source is null) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

        var watermark = await repo.GetWatermarkAsync(MinuteWatermark, ct)
                        ?? DateTimeOffset.UtcNow - TimeSpan.FromHours(1);

        var from = watermark - _options.UsageOverlap;
        var to = DateTimeOffset.UtcNow;

        var samples = await _source.GetUsageAsync(from, to, Granularity.Minute, ct);
        if (samples.Count == 0)
        {
            // Nothing to store, but still news: the live table's "Last minute" rates fall to zero.
            broker.PublishUsage(samples);
            return;
        }

        await repo.UpsertBucketsAsync(samples, ct);

        // Hold the watermark a minute behind now: the current minute is still filling.
        await repo.SetWatermarkAsync(MinuteWatermark, to - TimeSpan.FromMinutes(1), ct);

        broker.PublishUsage(samples);
    }

    /// <summary>
    /// Interval is re-read every tick so the idle backoff and any settings change take effect
    /// without restarting the loop.
    /// </summary>
    private async Task LoopAsync(
        Func<TimeSpan> interval, Func<CancellationToken, Task> work, string label, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Scale(interval()), ct);
                await work(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A bad tick must never kill the loop; the next one usually succeeds.
                log.LogError(ex, "The {Label} sampler tick failed.", label);
            }
        }
    }

    private TimeSpan Scale(TimeSpan interval) =>
        UserIdle.SinceLastInput() >= _options.IdleAfter
            ? interval * _options.IdleBackoffFactor
            : interval;

    private static ConnectionEventRecord ToRecord(ConnectionSnapshot c, DateTimeOffset at, ConnectionEventKind kind) =>
        new(at, kind, c.Pid, c.ProcessName, c.Protocol,
            c.Local.Address.ToString(), c.Local.Port,
            c.Remote?.Address.ToString(), c.Remote?.Port, c.State);
}
