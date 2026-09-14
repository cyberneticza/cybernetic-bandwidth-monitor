using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrace.Core.Abstractions;
using NetTrace.Core.Models;

namespace NetTrace.Core.Services;

/// <summary>
/// Answers "is it my network or the internet?" every half minute with two measurements:
///  - the router, by ping: if this is slow or failing, the problem is Wi-Fi or the local network;
///  - the internet, by a tiny HTTPS request: if only this fails, the problem is further out
///    (the provider, the modem's uplink, or a VPN or proxy in between).
///
/// A check costs a few kilobytes, about 10 MB a day at the default interval; on a metered connection only every
/// <see cref="MeteredEvery"/>th check runs. State changes only become events once they have held for
/// <see cref="HealthOptions.ConfirmChecks"/> checks, so one lost packet does not log an outage.
/// </summary>
public sealed class ConnectionHealthMonitor(
    IServiceScopeFactory scopes,
    LiveActivityBroker broker,
    NetworkEventRecorder recorder,
    MonitoringSettings settings,
    IOptions<HealthOptions> options,
    ILogger<ConnectionHealthMonitor> log) : BackgroundService
{
    /// <summary>On metered connections, one check in this many: every 5 minutes at the default interval.</summary>
    private const int MeteredEvery = 10;

    private readonly HealthOptions _options = options.Value;

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // Longer than the check interval, so each check reuses the warm connection and measures
        // the round trip rather than a fresh TLS handshake.
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        DefaultRequestHeaders = { { "User-Agent", "NetTrace-health/1.0" } }
    };

    private HealthStatus _reported = HealthStatus.Unknown;
    private HealthStatus _candidate = HealthStatus.Unknown;
    private int _candidateCount;
    private DateTimeOffset _reportedSince = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;
    private long _tick;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the network watcher publish its first snapshot.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        using var timer = new PeriodicTimer(_options.Interval);
        do
        {
            if (!settings.HealthChecksEnabled) continue;
            if (broker.LatestNetwork is { IsMetered: true } && _tick++ % MeteredEvery != 0) continue;

            try
            {
                var sample = await CheckAsync(ct);
                broker.PublishHealth(sample);
                await StoreAsync(sample, ct);
                await TrackTransitionsAsync(sample, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Connection health check failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<HealthSample> CheckAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var network = broker.LatestNetwork;

        if (network is { IsConnected: false })
            return new HealthSample(now, HealthStatus.Offline, null, null, null, 100);

        string? gateway = network?.Gateways.FirstOrDefault(g => IPAddress.TryParse(g, out var a) && a.AddressFamily == AddressFamily.InterNetwork);

        var gatewayTask = gateway is null ? Task.FromResult<double?>(null) : PingGatewayAsync(gateway);
        var (internetLatency, jitter, loss) = await ProbeInternetAsync(ct);
        double? gatewayLatency = await gatewayTask;

        var status = (internetLatency, gatewayLatency) switch
        {
            // The router answering while the internet does not is the one case worth telling apart:
            // a router that ignores ping says nothing either way, so no answer counts as offline.
            (null, not null) => HealthStatus.LocalOnly,
            (null, null) => HealthStatus.Offline,
            _ when internetLatency > _options.SlowLatencyMs || jitter > _options.SlowJitterMs || loss >= 34 => HealthStatus.Slow,
            _ => HealthStatus.Good
        };

        return new HealthSample(now, status, gatewayLatency, internetLatency, jitter, loss);
    }

    private static async Task<double?> PingGatewayAsync(string gateway)
    {
        using var ping = new Ping();
        var times = new List<double>(3);

        for (int i = 0; i < 3; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(gateway, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
            }
        }

        return RoundTrips.Median(times);
    }

    private async Task<(double? Latency, double? Jitter, double LossPercent)> ProbeInternetAsync(CancellationToken ct)
    {
        var times = new List<double>(_options.ProbesPerCheck);
        int failures = 0;

        // After a gap long enough for the pooled connection to have closed, one extra request goes
        // first and pays for the TLS handshake; otherwise the warm connection is measured directly.
        int first = DateTimeOffset.UtcNow - _lastProbe > TimeSpan.FromSeconds(90) ? 0 : 1;
        _lastProbe = DateTimeOffset.UtcNow;

        for (int i = first; i <= _options.ProbesPerCheck; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.ProbeTimeout);

            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await _http.GetAsync(_options.InternetProbeUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (i > 0) times.Add(ms);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (i > 0) failures++;
            }

            if (i < _options.ProbesPerCheck) await Task.Delay(200, ct);
        }

        double loss = 100.0 * failures / _options.ProbesPerCheck;
        return (RoundTrips.Median(times), RoundTrips.Jitter(times), loss);
    }

    private async Task StoreAsync(HealthSample sample, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>().InsertHealthSampleAsync(sample, ct);
    }

    private async Task TrackTransitionsAsync(HealthSample sample, CancellationToken ct)
    {
        // Disconnections are the network watcher's events; here only the internet's own state counts.
        if (broker.LatestNetwork is { IsConnected: false }) return;

        if (sample.Status == _candidate) _candidateCount++;
        else (_candidate, _candidateCount) = (sample.Status, 1);

        if (_candidate == _reported || _candidateCount < _options.ConfirmChecks) return;

        var previous = _reported;
        var lasted = NetworkEventRecorder.Duration(DateTimeOffset.UtcNow - _reportedSince);
        (_reported, _reportedSince) = (_candidate, DateTimeOffset.UtcNow);

        if (previous == HealthStatus.Unknown && _reported == HealthStatus.Good) return;

        bool wasDown = previous.IsDown();
        bool isDown = _reported.IsDown();

        if (isDown && !wasDown)
        {
            string detail = _reported == HealthStatus.LocalOnly
                ? "Your router answers, but the internet does not. The problem is likely beyond your network: the provider, the modem, or a VPN or proxy."
                : "Neither the router nor the internet answered.";
            await recorder.RecordAsync(NetworkEventKind.InternetUnreachable, "Internet unreachable", detail, ct);
        }
        else if (wasDown && !isDown)
        {
            await recorder.RecordAsync(NetworkEventKind.InternetRestored, "Internet reachable again", $"Unreachable for {lasted}.", ct);
        }
        else if (_reported == HealthStatus.Slow)
        {
            string detail = $"Round trip {sample.InternetLatencyMs:0} ms, jitter {sample.InternetJitterMs ?? 0:0} ms, " +
                            $"{sample.InternetLossPercent:0}% of requests failed.";
            await recorder.RecordAsync(NetworkEventKind.ConnectionSlow, "Connection slow", detail, ct);
        }
        else if (previous == HealthStatus.Slow && _reported == HealthStatus.Good)
        {
            await recorder.RecordAsync(NetworkEventKind.ConnectionRecovered, "Connection back to normal", $"Slow for {lasted}.", ct);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
