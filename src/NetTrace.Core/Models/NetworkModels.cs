namespace NetTrace.Core.Models;

public enum ConnectionMedium
{
    None = 0,
    Ethernet = 1,
    WiFi = 2,
    Cellular = 3,
    Other = 4
}

/// <summary>Windows' own verdict on reachability, as shown by the taskbar network icon.</summary>
public enum Reachability
{
    None = 0,
    LocalOnly = 1,
    /// <summary>Usually a captive portal: hotel or airport Wi-Fi waiting for a sign-in page.</summary>
    Limited = 2,
    Internet = 3
}

public enum RouteKind
{
    SecurityProxy,
    Vpn,
    ProxyServer
}

/// <summary>Software that carries this PC's traffic somewhere before it reaches the internet.</summary>
public sealed record TrafficRoute(string Name, RouteKind Kind)
{
    /// <summary>The kind in running text: "through this VPN", "Netskope Client (security proxy)".</summary>
    public string KindName => Kind switch
    {
        RouteKind.SecurityProxy => "security proxy",
        RouteKind.Vpn => "VPN",
        _ => "proxy server"
    };
}

/// <summary>What this PC is connected to right now. Rebuilt on every network change.</summary>
public sealed record NetworkSnapshot(
    DateTimeOffset ObservedUtc,
    ConnectionMedium Medium,
    string? NetworkName,
    string? AdapterName,
    long? LinkSpeedBitsPerSecond,
    int? SignalPercent,
    Reachability Reachability,
    bool IsMetered,
    bool IsApproachingDataLimit,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<TrafficRoute> Routes)
{
    public static NetworkSnapshot Disconnected(DateTimeOffset at) =>
        new(at, ConnectionMedium.None, null, null, null, null, Reachability.None, false, false, [], [], [], []);

    public bool IsConnected => Medium != ConnectionMedium.None && Reachability != Reachability.None;
}

/// <summary>Values are persisted; do not renumber.</summary>
public enum NetworkEventKind
{
    Disconnected = 0,
    Reconnected = 1,
    NetworkChanged = 2,
    RouteStarted = 3,
    RouteStopped = 4,
    InternetUnreachable = 5,
    InternetRestored = 6,
    ConnectionSlow = 7,
    ConnectionRecovered = 8,
    LimitedConnectivity = 9
}

public sealed record NetworkEventRecord(DateTimeOffset ObservedUtc, NetworkEventKind Kind, string Title, string? Detail)
{
    /// <summary>Events that mean something went wrong, as opposed to something merely changed.</summary>
    public bool IsProblem => Kind is NetworkEventKind.Disconnected or NetworkEventKind.InternetUnreachable
        or NetworkEventKind.ConnectionSlow or NetworkEventKind.LimitedConnectivity;
}

/// <summary>Values are persisted; do not renumber.</summary>
public enum HealthStatus
{
    Unknown = 0,
    Good = 1,
    Slow = 2,
    /// <summary>The router answers but the internet does not.</summary>
    LocalOnly = 3,
    Offline = 4
}

public static class HealthStatusExtensions
{
    /// <summary>The internet is not answering, whether or not the router is.</summary>
    public static bool IsDown(this HealthStatus status) => status is HealthStatus.Offline or HealthStatus.LocalOnly;
}

/// <summary>
/// One health check: a few requests to the router and to the internet a moment apart.
/// Latency is the median of the round trips that succeeded, jitter the mean difference between
/// consecutive ones, and loss the share that failed.
/// </summary>
public sealed record HealthSample(
    DateTimeOffset ObservedUtc,
    HealthStatus Status,
    double? GatewayLatencyMs,
    double? InternetLatencyMs,
    double? InternetJitterMs,
    double InternetLossPercent);

public sealed record SpeedTestResult(
    DateTimeOffset ObservedUtc,
    double DownloadMbps,
    double UploadMbps,
    double LatencyMs,
    double JitterMs,
    string Server,
    string? PublicAddress,
    string? NetworkName,
    ConnectionMedium Medium,
    long BytesUsed);

public enum SpeedTestPhase
{
    Latency,
    Download,
    Upload,
    Done
}

/// <summary>Reported a few times a second while a test runs.</summary>
public readonly record struct SpeedTestProgress(SpeedTestPhase Phase, double Fraction, double? CurrentMbps, double? LatencyMs);

/// <summary>A fixed-location test server, for measuring the connection to one part of the world.</summary>
public sealed record SpeedTestRegion(string Code, string City, string Country, string Continent, Uri TestFile);

/// <summary>
/// One region's result. Latency and download are null when that part failed; <see cref="Error"/>
/// says why. Regional servers take no uploads, so there is no upload figure.
/// </summary>
public sealed record RegionalSpeedResult(
    DateTimeOffset ObservedUtc,
    string RegionCode,
    double? LatencyMs,
    double? JitterMs,
    double? DownloadMbps,
    long BytesUsed,
    string? NetworkName,
    string? Error);

public readonly record struct RegionalProgress(string RegionCode, SpeedTestPhase Phase, double? CurrentMbps, double? LatencyMs);
