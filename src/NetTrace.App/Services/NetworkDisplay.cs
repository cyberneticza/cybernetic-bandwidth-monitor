using NetTrace.Core.Models;
using NetTrace.Core.Services;

namespace NetTrace.App.Services;

/// <summary>
/// The words and glyphs the Live and Network pages use for the connection, in one place so the
/// two pages never describe the same state differently.
/// </summary>
public static class NetworkDisplay
{
    public static string MediumGlyph(ConnectionMedium medium) => medium switch
    {
        ConnectionMedium.WiFi => "\uE701",
        ConnectionMedium.Ethernet => "\uE839",
        ConnectionMedium.Cellular => "\uE717",
        ConnectionMedium.None => "\uE7BA",
        _ => "\uE774"
    };

    public static string MediumName(ConnectionMedium medium) => medium switch
    {
        ConnectionMedium.WiFi => "Wi-Fi",
        ConnectionMedium.Ethernet => "Ethernet",
        ConnectionMedium.Cellular => "Mobile data",
        ConnectionMedium.None => "Not connected",
        _ => "Network"
    };

    public static string ConnectionName(NetworkSnapshot snapshot) =>
        snapshot.IsConnected ? snapshot.NetworkName ?? "Connected" : "Not connected";

    public static string LinkSpeed(long? bitsPerSecond) => bitsPerSecond switch
    {
        null or <= 0 => "unknown",
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1e6:0.#} Mbps",
        _ => $"{bitsPerSecond / 1e3:0} kbps"
    };

    public static string Signal(int? percent) => percent switch
    {
        null => "unknown",
        >= 80 => $"Excellent ({percent}%)",
        >= 60 => $"Good ({percent}%)",
        >= 40 => $"Fair ({percent}%)",
        _ => $"Weak ({percent}%)"
    };

    public static string Reachability(Reachability reachability) => reachability switch
    {
        Core.Models.Reachability.Internet => "Internet access",
        Core.Models.Reachability.Limited => "Limited: a sign-in page may be waiting",
        Core.Models.Reachability.LocalOnly => "Local network only",
        _ => "No connection"
    };

    /// <summary>The status to show: Unknown while checks are off, whatever the last sample said.</summary>
    public static HealthStatus Status(HealthSample? sample, bool checksEnabled) =>
        checksEnabled ? sample?.Status ?? HealthStatus.Unknown : HealthStatus.Unknown;

    public static string StatusName(HealthStatus status) => status switch
    {
        HealthStatus.Good => "Good",
        HealthStatus.Slow => "Slow",
        HealthStatus.LocalOnly => "Internet down",
        HealthStatus.Offline => "Offline",
        _ => "Checking…"
    };

    /// <summary>What the status means for the person at the keyboard, not for the network.</summary>
    public static string StatusMeaning(HealthSample? sample, bool checksEnabled, HealthOptions options)
    {
        if (!checksEnabled) return "Connection quality checks are turned off in Settings.";
        if (sample is null) return "The first check runs a few seconds after NetTrace starts.";

        return sample.Status switch
        {
            HealthStatus.Good => "Browsing, streaming and video calls should all feel normal.",
            HealthStatus.Slow => $"Pages may load slowly and calls may stutter: {SlowReason(sample, options)}.",
            HealthStatus.LocalOnly => "Your router answers but the internet does not. Restarting the modem or router usually fixes it; if not, the problem is with your provider, a VPN or a proxy.",
            HealthStatus.Offline => "Nothing is answering. Check the cable or Wi-Fi, or whether the network needs you to sign in.",
            _ => string.Empty
        };
    }

    private static string SlowReason(HealthSample sample, HealthOptions options)
    {
        var reasons = new List<string>();
        if (sample.InternetLatencyMs > options.SlowLatencyMs) reasons.Add($"responses take {sample.InternetLatencyMs:0} ms");
        if (sample.InternetJitterMs > options.SlowJitterMs) reasons.Add($"timing varies by {sample.InternetJitterMs:0} ms");
        if (sample.InternetLossPercent > 0) reasons.Add($"{sample.InternetLossPercent:0}% of requests failed");
        return reasons.Count == 0 ? "the connection is unsteady" : string.Join(", ", reasons);
    }

    public static string Milliseconds(double? ms) => ms is null ? "—" : $"{ms:0} ms";

    /// <summary>A row timestamp in the Network and Speed test history lists.</summary>
    public static string When(DateTimeOffset observedUtc) => observedUtc.LocalDateTime.ToString("ddd d MMM, HH:mm");

    public static string RouteExplanation(TrafficRoute route) => route.Kind switch
    {
        RouteKind.SecurityProxy => "Your organisation's security service inspects web traffic on its way out. Speed and latency include its servers, and most traffic is counted under it.",
        RouteKind.Vpn => "A VPN tunnel is up. Traffic travels through its server first, which adds latency and can lower speed.",
        _ => "Web traffic goes through a proxy server set in Windows."
    };

    /// <summary>A short label for a chart marker; the full title is in the Network page's list.</summary>
    public static string ShortEventLabel(NetworkEventKind kind) => kind switch
    {
        NetworkEventKind.Disconnected => "Disconnected",
        NetworkEventKind.Reconnected => "Reconnected",
        NetworkEventKind.NetworkChanged => "Switched network",
        NetworkEventKind.RouteStarted => "VPN/proxy on",
        NetworkEventKind.RouteStopped => "VPN/proxy off",
        NetworkEventKind.InternetUnreachable => "Internet down",
        NetworkEventKind.InternetRestored => "Internet back",
        NetworkEventKind.ConnectionSlow => "Slow",
        NetworkEventKind.ConnectionRecovered => "Back to normal",
        NetworkEventKind.LimitedConnectivity => "Limited",
        _ => kind.ToString()
    };

    public static string EventGlyph(NetworkEventKind kind) => kind switch
    {
        NetworkEventKind.Disconnected or NetworkEventKind.InternetUnreachable => "\uE7BA",
        NetworkEventKind.ConnectionSlow or NetworkEventKind.LimitedConnectivity => "\uE916",
        NetworkEventKind.NetworkChanged => "\uE8AB",
        NetworkEventKind.RouteStarted or NetworkEventKind.RouteStopped => "\uE72E",
        _ => "\uE73E"
    };
}
