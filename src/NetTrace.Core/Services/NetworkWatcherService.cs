using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Models;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace NetTrace.Core.Services;

/// <summary>
/// Keeps the current network snapshot fresh and turns the differences between snapshots into
/// events: dropped, back, switched networks, a VPN or proxy starting or stopping.
///
/// Windows raises a burst of change notifications for a single real change (an address, then a
/// route, then the profile), so notifications only schedule a re-read shortly afterwards. A slow
/// periodic re-read also runs, because signal strength changes without any notification at all.
/// </summary>
public sealed class NetworkWatcherService(
    NetworkEnvironment environment,
    NetworkEventRecorder recorder,
    LiveActivityBroker broker,
    ILogger<NetworkWatcherService> log) : BackgroundService
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Periodic = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _changed = new(0);
    private NetworkSnapshot? _previous;
    private DateTimeOffset? _disconnectedAt;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        void OnChange() { if (_changed.CurrentCount == 0) _changed.Release(); }

        NetworkAddressChangedEventHandler? addressHandler = null;
        Windows.Networking.Connectivity.NetworkStatusChangedEventHandler? statusHandler = null;

        try
        {
            addressHandler = (_, _) => OnChange();
            NetworkChange.NetworkAddressChanged += addressHandler;

            statusHandler = _ => OnChange();
            WinRtNetworkInformation.NetworkStatusChanged += statusHandler;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Network change notifications are unavailable; relying on periodic checks.");
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadAndCompareAsync(ct);

                // Wait for a notification or the periodic tick, whichever is first; after a
                // notification, give the burst time to finish before reading.
                if (await _changed.WaitAsync(Periodic, ct))
                {
                    await Task.Delay(Settle, ct);
                    while (_changed.CurrentCount > 0) await _changed.WaitAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (addressHandler is not null) NetworkChange.NetworkAddressChanged -= addressHandler;
            if (statusHandler is not null) WinRtNetworkInformation.NetworkStatusChanged -= statusHandler;
        }
    }

    private async Task ReadAndCompareAsync(CancellationToken ct)
    {
        try
        {
            var next = environment.Read();
            // Most periodic reads find nothing new; every page listening would redraw for nothing.
            if (_previous is null || !SameConnection(_previous, next)) broker.PublishNetwork(next);

            if (_previous is { } previous) await RecordDifferencesAsync(previous, next, ct);
            else if (!next.IsConnected) _disconnectedAt = next.ObservedUtc;

            _previous = next;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Network check failed.");
        }
    }

    private async Task RecordDifferencesAsync(NetworkSnapshot before, NetworkSnapshot after, CancellationToken ct)
    {
        string Describe(NetworkSnapshot s) => $"{s.NetworkName ?? "network"} ({MediumName(s.Medium)})";

        if (before.IsConnected && !after.IsConnected)
        {
            _disconnectedAt = after.ObservedUtc;
            await recorder.RecordAsync(NetworkEventKind.Disconnected, $"Disconnected from {Describe(before)}", null, ct);
        }
        else if (!before.IsConnected && after.IsConnected)
        {
            string? detail = _disconnectedAt is { } since
                ? $"Offline for {NetworkEventRecorder.Duration(after.ObservedUtc - since)}."
                : null;
            _disconnectedAt = null;
            await recorder.RecordAsync(NetworkEventKind.Reconnected, $"Connected to {Describe(after)}", detail, ct);
        }
        else if (before.IsConnected && after.IsConnected
                 && (before.NetworkName != after.NetworkName || before.Medium != after.Medium))
        {
            await recorder.RecordAsync(NetworkEventKind.NetworkChanged,
                $"Switched to {Describe(after)}", $"Was on {Describe(before)}.", ct);
        }

        if (after.IsConnected && before.Reachability == Reachability.Internet && after.Reachability == Reachability.Limited)
        {
            await recorder.RecordAsync(NetworkEventKind.LimitedConnectivity, "Limited connectivity",
                "Windows reports restricted internet access. A sign-in page (captive portal) may be waiting.", ct);
        }

        foreach (var route in after.Routes.ExceptBy(before.Routes.Select(r => r.Name), r => r.Name))
        {
            await recorder.RecordAsync(NetworkEventKind.RouteStarted, $"{route.Name} started",
                $"Traffic may now pass through this {route.KindName} before reaching the internet.", ct);
        }

        foreach (var route in before.Routes.ExceptBy(after.Routes.Select(r => r.Name), r => r.Name))
        {
            await recorder.RecordAsync(NetworkEventKind.RouteStopped, $"{route.Name} stopped", null, ct);
        }
    }

    /// <summary>Equal in everything but the time read. Record equality compares the lists by reference.</summary>
    private static bool SameConnection(NetworkSnapshot a, NetworkSnapshot b) =>
        a with { ObservedUtc = b.ObservedUtc, Addresses = b.Addresses, Gateways = b.Gateways, DnsServers = b.DnsServers, Routes = b.Routes } == b
        && a.Addresses.SequenceEqual(b.Addresses)
        && a.Gateways.SequenceEqual(b.Gateways)
        && a.DnsServers.SequenceEqual(b.DnsServers)
        && a.Routes.SequenceEqual(b.Routes);

    private static string MediumName(ConnectionMedium medium) => medium switch
    {
        ConnectionMedium.WiFi => "Wi-Fi",
        ConnectionMedium.Ethernet => "Ethernet",
        ConnectionMedium.Cellular => "mobile data",
        ConnectionMedium.None => "not connected",
        _ => "other"
    };
}
