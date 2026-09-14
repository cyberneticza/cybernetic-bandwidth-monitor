using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Interop;
using NetTrace.Core.Models;
using Windows.Networking.Connectivity;

namespace NetTrace.Core.Services;

/// <summary>
/// Reads what this PC is connected to right now, from three places a standard user can see:
/// Windows' connection profiles (network name, reachability, cost), the adapters themselves
/// (addresses, gateway, DNS, link speed) and the Native Wifi API (signal quality).
///
/// It also looks for software that carries traffic somewhere before it reaches the internet.
/// That matters to everything else NetTrace shows: through a VPN or a security proxy, latency,
/// speed test results and the per-app byte counts all describe the tunnel, not the raw line.
/// </summary>
public sealed partial class NetworkEnvironment(ILogger<NetworkEnvironment> log)
{
    // Security proxies steer traffic without an adapter of their own, so a running agent is
    // the only visible sign. VPN clients are different: their service runs all the time and
    // only an adapter that is up means the tunnel is.
    private static readonly (string Process, string Name)[] SecurityProxies =
    [
        ("stAgentSvc", "Netskope Client"),
        ("ZSATunnel", "Zscaler Client Connector"),
        ("warp-svc", "Cloudflare WARP"),
        ("ForcepointNetworkProxy", "Forcepoint One Endpoint")
    ];

    private static readonly (string Match, string Name)[] VpnAdapters =
    [
        ("Fortinet", "FortiClient VPN"),
        ("PANGP", "GlobalProtect"),
        ("AnyConnect", "Cisco Secure Client"),
        ("Cisco Secure", "Cisco Secure Client"),
        ("WireGuard", "WireGuard"),
        ("Wintun", "WireGuard-based VPN"),
        ("TAP-Windows", "OpenVPN"),
        ("OpenVPN", "OpenVPN"),
        ("Tailscale", "Tailscale"),
        ("ZeroTier", "ZeroTier"),
        ("NordLynx", "NordVPN"),
        ("Juniper", "Juniper Secure Connect"),
        ("SonicWall", "SonicWall NetExtender"),
        ("VPN", "VPN")
    ];

    private static readonly Uri ProxyProbe = new("https://www.cloudflare.com/");

    public NetworkSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            var primary = FindPrimary(profile, adapters);

            if (primary is null) return NetworkSnapshot.Disconnected(now) with { Routes = FindRoutes(adapters) };

            var medium = MediumOf(primary, profile);
            var ip = primary.GetIPProperties();
            var (metered, nearLimit) = Optional(() => CostOf(profile), (false, false), "connection cost");

            return new NetworkSnapshot(
                ObservedUtc: now,
                Medium: medium,
                NetworkName: Optional(() => NameOf(profile, primary), primary.Name, "network name"),
                AdapterName: primary.Description,
                LinkSpeedBitsPerSecond: primary.Speed > 0 ? primary.Speed : null,
                SignalPercent: medium == ConnectionMedium.WiFi ? Optional(() => SignalOf(primary, profile), null, "signal strength") : null,
                Reachability: ReachabilityOf(profile),
                IsMetered: metered,
                IsApproachingDataLimit: nearLimit,
                Addresses: ip.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                || (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal))
                    .OrderBy(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.ToString())
                    .ToList(),
                Gateways: ip.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString())
                    .ToList(),
                DnsServers: ip.DnsAddresses
                    .Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal))
                    .Select(a => a.ToString())
                    .Distinct()
                    .ToList(),
                Routes: FindRoutes(adapters));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the current network configuration.");
            return NetworkSnapshot.Disconnected(now);
        }
    }

    /// <summary>
    /// For details a standard user may be refused: a failure costs that one value, never the
    /// snapshot, which would otherwise read as a disconnect and log a false event.
    /// </summary>
    private T Optional<T>(Func<T> read, T fallback, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read the {What}.", what);
            return fallback;
        }
    }

    /// <summary>
    /// The adapter Windows routes internet traffic through: the one behind the internet connection
    /// profile, or failing that the fastest physical adapter that is up and has a gateway.
    /// </summary>
    private static NetworkInterface? FindPrimary(ConnectionProfile? profile, NetworkInterface[] adapters)
    {
        var candidates = adapters
            .Where(a => a.OperationalStatus == OperationalStatus.Up && IsRealAdapter(a)
                        && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();

        if (profile?.NetworkAdapter?.NetworkAdapterId is { } id)
        {
            var match = candidates.FirstOrDefault(a => Guid.TryParse(a.Id, out var g) && g == id);
            if (match is not null) return match;
        }

        return candidates
            .Where(a => a.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .OrderByDescending(a => a.Speed)
            .FirstOrDefault();
    }

    private static ConnectionMedium MediumOf(NetworkInterface adapter, ConnectionProfile? profile)
    {
        if (profile?.IsWlanConnectionProfile == true) return ConnectionMedium.WiFi;
        if (profile?.IsWwanConnectionProfile == true) return ConnectionMedium.Cellular;

        return adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => ConnectionMedium.WiFi,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => ConnectionMedium.Cellular,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => ConnectionMedium.Ethernet,
            _ => ConnectionMedium.Other
        };
    }

    private static string NameOf(ConnectionProfile? profile, NetworkInterface adapter)
    {
        if (profile?.IsWlanConnectionProfile == true)
        {
            string? ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            if (!string.IsNullOrWhiteSpace(ssid)) return ssid;
        }

        // The name Windows gave the network: a domain on corporate networks, "Network 2" at home.
        if (Guid.TryParse(adapter.Id, out var id) && NetworkListManager.GetNamesByAdapter().TryGetValue(id, out var name))
            return name;

        return string.IsNullOrWhiteSpace(profile?.ProfileName) ? adapter.Name : profile.ProfileName;
    }

    /// <summary>
    /// .NET lists every filter driver bound to an adapter as an interface of its own
    /// ("Ethernet-Fortinet NDIS 6.0 LightWeight Filter-0000"), plus the WAN miniports, and many of
    /// them report Up. None of them is a connection, and matching VPN names against them would
    /// report a VPN whenever its driver is merely installed.
    /// </summary>
    private static bool IsRealAdapter(NetworkInterface adapter) =>
        adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
        && !FilterSuffix().IsMatch(adapter.Name)
        && !adapter.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{4}$")]
    private static partial System.Text.RegularExpressions.Regex FilterSuffix();

    private int? SignalOf(NetworkInterface adapter, ConnectionProfile? profile)
    {
        try
        {
            var link = WlanApi.GetConnectedLinks()
                .FirstOrDefault(l => Guid.TryParse(adapter.Id, out var g) && g == l.InterfaceId);
            if (link.InterfaceId != Guid.Empty) return link.SignalPercent;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Native Wifi query failed; falling back to signal bars.");
        }

        // Bars are coarse (0 to 5) but need no location permission.
        byte? bars = profile?.GetSignalBars();
        return bars is null ? null : bars.Value * 20;
    }

    private static Reachability ReachabilityOf(ConnectionProfile? profile) => profile?.GetNetworkConnectivityLevel() switch
    {
        NetworkConnectivityLevel.InternetAccess => Reachability.Internet,
        NetworkConnectivityLevel.ConstrainedInternetAccess => Reachability.Limited,
        NetworkConnectivityLevel.LocalAccess => Reachability.LocalOnly,
        _ => Reachability.None
    };

    private static (bool Metered, bool NearLimit) CostOf(ConnectionProfile? profile)
    {
        var cost = profile?.GetConnectionCost();
        if (cost is null) return (false, false);

        return (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming,
                cost.ApproachingDataLimit || cost.OverDataLimit);
    }

    private static HashSet<string> RunningProcessNames()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private IReadOnlyList<TrafficRoute> FindRoutes(NetworkInterface[] adapters)
    {
        var routes = new List<TrafficRoute>();

        // One process snapshot for all of them.
        var running = Optional<HashSet<string>>(RunningProcessNames, [], "running processes");
        routes.AddRange(SecurityProxies
            .Where(p => running.Contains(p.Process))
            .Select(p => new TrafficRoute(p.Name, RouteKind.SecurityProxy)));

        foreach (var adapter in adapters.Where(a => a.OperationalStatus == OperationalStatus.Up && IsRealAdapter(a)))
        {
            bool tunnelLike = adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel;
            string text = $"{adapter.Description} {adapter.Name}";

            // Windows' own IPv6 transition tunnels are always up and carry nothing worth naming.
            if (text.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                || text.Contains("isatap", StringComparison.OrdinalIgnoreCase)
                || text.Contains("6to4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("IP-HTTPS", StringComparison.OrdinalIgnoreCase))
                continue;

            var known = VpnAdapters.FirstOrDefault(v => text.Contains(v.Match, StringComparison.OrdinalIgnoreCase));
            if (known.Name is not null) routes.Add(new TrafficRoute(known.Name, RouteKind.Vpn));
            else if (tunnelLike) routes.Add(new TrafficRoute(adapter.Description, RouteKind.Vpn));
        }

        var proxy = Optional(() => HttpClient.DefaultProxy.GetProxy(ProxyProbe), null, "system proxy");
        if (proxy is not null && proxy != ProxyProbe) routes.Add(new TrafficRoute(proxy.Host, RouteKind.ProxyServer));

        return routes.DistinctBy(r => r.Name).ToList();
    }
}
