using System.Diagnostics;
using System.Net.NetworkInformation;
using CyberneticBandwidth.Core.Abstractions;

namespace CyberneticBandwidth.Core.Sources;

/// <summary>
/// Machine-wide throughput from NIC counters. This is not attributed to any process — it exists
/// because NDU only refreshes on roughly minute boundaries, and the dashboard needs something
/// that moves when you start a download.
/// </summary>
public sealed class InterfaceThroughputSource : IThroughputSource
{
    private long _lastReceived;
    private long _lastSent;
    private long _lastTimestamp;

    public (double DownloadBytesPerSecond, double UploadBytesPerSecond) SampleRate()
    {
        long received = 0, sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var stats = nic.GetIPStatistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        long now = Stopwatch.GetTimestamp();

        if (_lastTimestamp == 0)
        {
            (_lastReceived, _lastSent, _lastTimestamp) = (received, sent, now);
            return (0, 0);
        }

        double seconds = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        if (seconds <= 0) return (0, 0);

        // Counters reset when an adapter is disabled or the machine resumes; clamp rather than
        // reporting a negative or absurd spike.
        double down = System.Math.Max(0, received - _lastReceived) / seconds;
        double up = System.Math.Max(0, sent - _lastSent) / seconds;

        (_lastReceived, _lastSent, _lastTimestamp) = (received, sent, now);
        return (down, up);
    }
}
