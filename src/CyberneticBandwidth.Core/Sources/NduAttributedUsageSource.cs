using Microsoft.Extensions.Logging;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;
using Windows.Networking.Connectivity;

namespace CyberneticBandwidth.Core.Sources;

/// <summary>
/// Per-application byte counts from the Windows Data Usage (NDU) subsystem — the same data
/// Settings > Network > Data usage shows. This is the only per-app byte source available to a
/// standard user, because the service doing the measuring is already running elevated on our
/// behalf.
///
/// Two things to keep in mind:
///  - Resolution is about a minute. Anything finer is invented.
///  - Docs list the networkDataPlanProvisioning capability. Unpackaged Win32 usually still gets
///    data, which is exactly why IsAvailableAsync probes instead of assuming.
/// </summary>
public sealed class NduAttributedUsageSource(
    AppNameResolver names,
    ILogger<NduAttributedUsageSource> log) : INetworkUsageSource
{
    public string Name => "Windows Data Usage (NDU)";

    public int Priority => 100;

    private static NetworkUsageStates AnyState => new()
    {
        Roaming = TriStates.DoNotCare,
        Shared = TriStates.DoNotCare
    };

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var end = DateTimeOffset.UtcNow;
            var start = end - TimeSpan.FromHours(6);

            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                var usage = await profile.GetAttributedNetworkUsageAsync(start, end, AnyState).AsTask(ct);
                if (usage.Count > 0)
                {
                    log.LogInformation("NDU attribution is available ({Count} apps in the probe window).", usage.Count);
                    return true;
                }
            }

            log.LogWarning(
                "NDU returned no attributed usage over the last six hours. Either this machine has " +
                "genuinely been idle, or attribution is blocked for an unpackaged process.");
            return false;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "NDU attributed usage is unavailable on this machine.");
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<UsageSample>> GetUsageAsync(
        DateTimeOffset from, DateTimeOffset to, Granularity granularity, CancellationToken ct)
    {
        var step = granularity.BucketSize();
        var profiles = NetworkInformation.GetConnectionProfiles();

        // Several profiles classify to the same interface kind (every Wi-Fi network ever joined,
        // Ethernet plus a Bluetooth PAN, ...), and the store keys buckets by app, bucket and kind.
        // Summed here so the rows it upserts are whole: one row per profile would make each
        // write replace the last, and the app would keep whichever profile happened to come last.
        var totals = new Dictionary<(string Id, DateTimeOffset Slot, InterfaceKind Kind), (string Name, long Sent, long Received)>();

        for (var slot = from.FloorTo(granularity); slot < to; slot += step)
        {
            var slotEnd = slot + step;

            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<AttributedNetworkUsage> usage;
                try
                {
                    usage = await profile.GetAttributedNetworkUsageAsync(slot, slotEnd, AnyState).AsTask(ct);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Usage query failed for the bucket starting {Slot:u}.", slot);
                    continue;
                }

                var kind = Classify(profile);

                foreach (var u in usage)
                {
                    if (u.BytesSent == 0 && u.BytesReceived == 0) continue;

                    // Existing rows key on "name:" for traffic with no id, so that stays as is.
                    string id = string.IsNullOrEmpty(u.AttributionId) ? $"name:{u.AttributionName}" : u.AttributionId;

                    // AttributionName is only populated for packaged callers; Cybernetic Bandwidth Monitor is not one.
                    string name = string.IsNullOrWhiteSpace(u.AttributionName)
                        ? names.Resolve(u.AttributionId)
                        : u.AttributionName;

                    var key = (id, slot, kind);
                    totals.TryGetValue(key, out var sum);
                    totals[key] = (name, sum.Sent + (long)u.BytesSent, sum.Received + (long)u.BytesReceived);
                }
            }
        }

        return totals
            .Select(t => new UsageSample(
                AttributionId: t.Key.Id,
                DisplayName: t.Value.Name,
                BucketStartUtc: t.Key.Slot,
                Granularity: granularity,
                Interface: t.Key.Kind,
                BytesSent: t.Value.Sent,
                BytesReceived: t.Value.Received))
            .ToList();
    }

    private static InterfaceKind Classify(ConnectionProfile profile)
    {
        if (profile.IsWwanConnectionProfile) return InterfaceKind.Cellular;
        if (profile.IsWlanConnectionProfile) return InterfaceKind.WiFi;
        return profile.NetworkAdapter is null ? InterfaceKind.Unknown : InterfaceKind.Ethernet;
    }
}
