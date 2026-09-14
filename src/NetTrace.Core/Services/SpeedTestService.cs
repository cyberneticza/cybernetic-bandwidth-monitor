using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Abstractions;
using NetTrace.Core.Models;

namespace NetTrace.Core.Services;

/// <summary>
/// Two kinds of speed test that share one way of measuring:
///  - <b>Nearest</b>: Cloudflare's public speed-test endpoints (the ones behind speed.cloudflare.com)
///    for latency, download and upload. Cloudflare is anycast, so this always reaches the closest
///    data centre and cannot be pointed anywhere else.
///  - <b>Regional</b>: public test files on Vultr servers in fixed cities across the continents,
///    for latency and download to that part of the world. They accept no uploads.
///
/// Downloads run for a fixed time rather than a fixed size, growing the request size while
/// requests finish quickly, so a slow line is not stuck on a huge file and a fast one is not
/// measured on tiny ones. The first stretch is left out of the result: TCP starts slowly and every
/// connection spends its first moments ramping up.
///
/// Results describe the path this PC's traffic really takes, including any VPN or security proxy in
/// between, which is usually what the user experiences.
/// </summary>
public sealed class SpeedTestService(
    IServiceScopeFactory scopes,
    LiveActivityBroker broker,
    ILogger<SpeedTestService> log)
{
    private const string CloudflareUrl = "https://speed.cloudflare.com";
    private const int LatencyProbes = 10;
    private const int RegionalLatencyProbes = 5;
    private const int MaxUploadChunk = 25_000_000;
    private const long RegionalFileBytes = 100_000_000;

    private static readonly TimeSpan DownloadBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RegionalDownloadBudget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// A distant server behind a buffering proxy can take many seconds to start answering; past this
    /// the region is reported with whatever arrived rather than holding up the rest of the run.
    /// </summary>
    private static readonly TimeSpan RegionalDeadline = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan RampUp = TimeSpan.FromSeconds(1.5);
    private const long DownloadByteCap = 500_000_000;
    private const long UploadByteCap = 150_000_000;

    /// <summary>
    /// Vultr's public speed-test hosts, spread across continents and ordered roughly by distance from
    /// southern Africa. Each serves the same 100 MB file and honours range requests.
    /// </summary>
    public static IReadOnlyList<SpeedTestRegion> Regions { get; } =
    [
        Vultr("jnb-za", "Johannesburg", "South Africa", "Africa"),
        Vultr("lon-gb", "London", "United Kingdom", "Europe"),
        Vultr("fra-de", "Frankfurt", "Germany", "Europe"),
        Vultr("bom-in", "Mumbai", "India", "Asia"),
        Vultr("sgp", "Singapore", "Singapore", "Asia"),
        Vultr("nj-us", "New Jersey", "United States", "North America"),
        Vultr("sao-br", "São Paulo", "Brazil", "South America"),
        Vultr("lax-ca-us", "Los Angeles", "United States", "North America"),
        Vultr("hnd-jp", "Tokyo", "Japan", "Asia"),
        Vultr("syd-au", "Sydney", "Australia", "Oceania")
    ];

    private static SpeedTestRegion Vultr(string code, string city, string country, string continent) =>
        new(code, city, country, continent, new Uri($"https://{code}-ping.vultr.com/vultr.com.100MB.bin"));

    private readonly SemaphoreSlim _running = new(1, 1);

    // ================================================================ nearest (Cloudflare)

    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
    {
        await using var slot = await TakeSlotAsync(ct);
        using var http = CreateClient();

        string server = "Cloudflare";
        string? address = null;

        var times = await ProbeLatencyAsync(http, () => new HttpRequestMessage(HttpMethod.Get, $"{CloudflareUrl}/__down?bytes=0"),
            warmUps: 1, probes: LatencyProbes,
            onFirst: response =>
            {
                // CF-RAY ends in the code of the data centre that answered, e.g. "…-JNB".
                if (response.Headers.TryGetValues("cf-ray", out var ray) && ray.First().Split('-') is [_, .., var colo])
                    server = $"Cloudflare {colo}";
                if (response.Headers.TryGetValues("cf-meta-ip", out var ip)) address = ip.First();
            },
            onProbe: sofar => progress.Report(new SpeedTestProgress(
                SpeedTestPhase.Latency, 0.1 * sofar.Count / LatencyProbes, null, RoundTrips.Median(sofar))),
            ct);

        // Every probe either succeeded or threw, so there are always LatencyProbes times.
        double latency = RoundTrips.Median(times) ?? 0;
        double jitter = RoundTrips.Jitter(times) ?? 0;

        var (download, downloaded) = await MeasureDownloadAsync(http,
            size => new HttpRequestMessage(HttpMethod.Get, $"{CloudflareUrl}/__down?bytes={size}"),
            DownloadBudget, maxRequestBytes: 100_000_000, deadline: CancellationToken.None,
            (elapsed, mbps) => progress.Report(new SpeedTestProgress(
                SpeedTestPhase.Download, 0.1 + 0.5 * Math.Min(1, elapsed / DownloadBudget), mbps, latency)),
            ct);

        var (upload, uploaded) = await MeasureUploadAsync(http, progress, latency, ct);

        progress.Report(new SpeedTestProgress(SpeedTestPhase.Done, 1, null, latency));

        var network = broker.LatestNetwork;
        var result = new SpeedTestResult(
            DateTimeOffset.UtcNow, download, upload, latency, jitter, server, address,
            network?.NetworkName, network?.Medium ?? ConnectionMedium.Other, downloaded + uploaded);

        log.LogInformation("Speed test: {Down:0.0} Mbps down, {Up:0.0} Mbps up, {Latency:0} ms, via {Server}.",
            download, upload, latency, server);

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>().InsertSpeedTestAsync(result, ct);
        }

        return result;
    }

    // ================================================================ regional (Vultr)

    /// <summary>
    /// Tests each region in turn: one at a time, so regions never compete for the line. A region
    /// that fails is recorded with its error and the run moves on; only cancellation stops it.
    /// </summary>
    public async Task<IReadOnlyList<RegionalSpeedResult>> RunRegionsAsync(
        IReadOnlyList<SpeedTestRegion> regions, IProgress<RegionalProgress> progress, CancellationToken ct)
    {
        await using var slot = await TakeSlotAsync(ct);
        using var http = CreateClient();

        var results = new List<RegionalSpeedResult>(regions.Count);
        foreach (var region in regions)
        {
            ct.ThrowIfCancellationRequested();
            var result = await TestRegionAsync(http, region, progress, ct);
            results.Add(result);

            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>().InsertRegionalSpeedTestAsync(result, ct);
        }
        return results;
    }

    private async Task<RegionalSpeedResult> TestRegionAsync(
        HttpClient http, SpeedTestRegion region, IProgress<RegionalProgress> progress, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(RegionalDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        HttpRequestMessage Ranged(long bytes)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, region.TestFile);
            request.Headers.Range = new RangeHeaderValue(0, Math.Min(bytes, RegionalFileBytes) - 1);
            return request;
        }

        double? latency = null, jitter = null, download = null;
        long bytes = 0;

        try
        {
            progress.Report(new RegionalProgress(region.Code, SpeedTestPhase.Latency, null, null));

            // Two warm-ups: the first opens the connection, and through a proxy the second still pays
            // for the proxy's own connection onward to the server.
            var times = await ProbeLatencyAsync(http, () => Ranged(1), warmUps: 2, probes: RegionalLatencyProbes,
                onFirst: null,
                onProbe: sofar => progress.Report(new RegionalProgress(
                    region.Code, SpeedTestPhase.Latency, null, RoundTrips.Median(sofar))),
                linked.Token);
            latency = RoundTrips.Median(times);
            jitter = RoundTrips.Jitter(times);

            (download, bytes) = await MeasureDownloadAsync(http, Ranged, RegionalDownloadBudget,
                maxRequestBytes: RegionalFileBytes, deadline: deadline.Token,
                (_, mbps) => progress.Report(new RegionalProgress(region.Code, SpeedTestPhase.Download, mbps, latency)),
                linked.Token);

            // A distant server can use up the deadline before any data arrives. That is no measurement,
            // not a speed of zero, which would drag down comparisons and the continent verdicts.
            string? problem = null;
            if (download is not > 0)
            {
                download = null;
                problem = "Download did not start in time";
            }

            progress.Report(new RegionalProgress(region.Code, SpeedTestPhase.Done, download, latency));
            log.LogInformation("Regional speed test {Region}: {Latency:0} ms, {Down:0.0} Mbps down.", region.City, latency, download);
            return new RegionalSpeedResult(DateTimeOffset.UtcNow, region.Code, latency, jitter, download, bytes,
                broker.LatestNetwork?.NetworkName, problem);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Whatever was measured before the failure is kept; a latency without a download is still
            // worth showing.
            string error = deadline.IsCancellationRequested ? "No answer in time" : "Could not reach the server";
            log.LogWarning(ex, "Regional speed test {Region} failed.", region.City);
            progress.Report(new RegionalProgress(region.Code, SpeedTestPhase.Done, download, latency));
            return new RegionalSpeedResult(DateTimeOffset.UtcNow, region.Code, latency, jitter, download, bytes,
                broker.LatestNetwork?.NetworkName, error);
        }
    }

    // ================================================================ shared measurement

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "NetTrace-speedtest/1.0" } }
    };

    /// <summary>One test at a time across both kinds: two tests at once would each measure half a line.</summary>
    private async Task<IAsyncDisposable> TakeSlotAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) throw new InvalidOperationException("A speed test is already running.");
        return new Releaser(_running);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Round trips to the first response byte on a warm connection; warm-ups are not counted.</summary>
    private static async Task<List<double>> ProbeLatencyAsync(
        HttpClient http, Func<HttpRequestMessage> request, int warmUps, int probes,
        Action<HttpResponseMessage>? onFirst, Action<List<double>> onProbe, CancellationToken ct)
    {
        var times = new List<double>(probes);

        for (int i = 0; i < warmUps + probes; i++)
        {
            using var message = request();
            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            double ms = sw.Elapsed.TotalMilliseconds;

            if (i == 0) onFirst?.Invoke(response);
            if (i < warmUps) continue;

            times.Add(ms);
            onProbe(times);
        }

        return times;
    }

    /// <summary>
    /// Downloads for <paramref name="budget"/>, growing requests from 1 MB until one takes about a
    /// second. When <paramref name="deadline"/> fires mid-request, the bytes that did arrive still count.
    /// </summary>
    private static async Task<(double Mbps, long Bytes)> MeasureDownloadAsync(
        HttpClient http, Func<long, HttpRequestMessage> request, TimeSpan budget, long maxRequestBytes,
        CancellationToken deadline, Action<TimeSpan, double> onProgress, CancellationToken ct)
    {
        var meter = new RateMeter();
        long size = 1_000_000;
        var buffer = new byte[81_920];
        var clock = Stopwatch.StartNew();

        try
        {
            while (clock.Elapsed < budget && meter.TotalBytes < DownloadByteCap)
            {
                var requestClock = Stopwatch.StartNew();
                using var message = request(size);
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);

                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    meter.Add(clock.Elapsed, read);
                    if (meter.ShouldReport(clock.Elapsed)) onProgress(clock.Elapsed, meter.RecentMbps(clock.Elapsed));
                    if (clock.Elapsed >= budget) break;
                }

                // Quick requests say the line can take more: grow until one takes about a second.
                if (requestClock.Elapsed < TimeSpan.FromSeconds(1)) size = Math.Min(size * 4, maxRequestBytes);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && meter.TotalBytes > 0)
        {
            // Out of time with part of the answer in hand: report what arrived.
        }

        return (meter.SteadyMbps(clock.Elapsed), meter.TotalBytes);
    }

    private static async Task<(double Mbps, long Bytes)> MeasureUploadAsync(
        HttpClient http, IProgress<SpeedTestProgress> progress, double latencyMs, CancellationToken ct)
    {
        int size = 1_000_000;
        byte[] payload = [];
        long sentBytes = 0;
        var steadyBytes = 0L;
        var steadyTime = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < UploadBudget && sentBytes < UploadByteCap)
        {
            // Grown with the request size, so a slow line never pays for the largest chunk. Random,
            // so no compressing proxy on the way can make the upload look faster than it is.
            if (payload.Length < size)
            {
                payload = new byte[size];
                RandomNumberGenerator.Fill(payload);
            }

            using var content = new ByteArrayContent(payload, 0, size);
            content.Headers.ContentType = new("application/octet-stream");

            var requestClock = Stopwatch.StartNew();
            var started = clock.Elapsed;
            using var response = await http.PostAsync($"{CloudflareUrl}/__up", content, ct);
            response.EnsureSuccessStatusCode();
            var took = requestClock.Elapsed;

            sentBytes += size;

            // Timed from send to response, which includes the server's acknowledgement; bytes
            // merely handed to the local socket buffer would read as far faster than the line.
            if (started >= RampUp || clock.Elapsed >= UploadBudget)
            {
                steadyBytes += size;
                steadyTime += took;
            }

            double mbps = size * 8 / took.TotalSeconds / 1_000_000;
            double fraction = 0.6 + 0.4 * Math.Min(1, clock.Elapsed / UploadBudget);
            progress.Report(new SpeedTestProgress(SpeedTestPhase.Upload, fraction, mbps, latencyMs));

            if (took < TimeSpan.FromSeconds(1)) size = Math.Min(size * 4, MaxUploadChunk);
        }

        double result = steadyTime > TimeSpan.Zero
            ? steadyBytes * 8 / steadyTime.TotalSeconds / 1_000_000
            : sentBytes * 8 / clock.Elapsed.TotalSeconds / 1_000_000;

        return (result, sentBytes);
    }

    /// <summary>Bytes against the test clock, for a live reading and a steady-state result.</summary>
    private sealed class RateMeter
    {
        private readonly Queue<(TimeSpan At, long Bytes)> _recent = new();
        private TimeSpan _lastReport;
        private long _steadyBytes;
        private TimeSpan? _steadyFrom;

        public long TotalBytes { get; private set; }

        public void Add(TimeSpan at, int bytes)
        {
            TotalBytes += bytes;
            _recent.Enqueue((at, bytes));
            // Two seconds: a proxy that buffers each response delivers it in bursts, and a shorter window
            // would swing between nothing and a spike instead of showing the rate.
            while (_recent.Count > 0 && at - _recent.Peek().At > TimeSpan.FromSeconds(2)) _recent.Dequeue();

            if (at >= RampUp)
            {
                _steadyFrom ??= at;
                _steadyBytes += bytes;
            }
        }

        public bool ShouldReport(TimeSpan at)
        {
            if (at - _lastReport < TimeSpan.FromMilliseconds(200)) return false;
            _lastReport = at;
            return true;
        }

        public double RecentMbps(TimeSpan at)
        {
            if (_recent.Count == 0) return 0;
            double seconds = Math.Max(0.2, (at - _recent.Peek().At).TotalSeconds);
            return _recent.Sum(r => r.Bytes) * 8 / seconds / 1_000_000;
        }

        public double SteadyMbps(TimeSpan end)
        {
            if (_steadyFrom is { } from && end - from > TimeSpan.FromMilliseconds(500))
                return _steadyBytes * 8 / (end - from).TotalSeconds / 1_000_000;

            // The whole test was shorter than the ramp-up; the average is all there is.
            return TotalBytes * 8 / Math.Max(0.001, end.TotalSeconds) / 1_000_000;
        }
    }
}
