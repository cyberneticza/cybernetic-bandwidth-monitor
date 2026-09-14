using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Abstractions;

/// <summary>Point-in-time view of every socket owned by a process on this machine.</summary>
public interface IConnectionSource
{
    IReadOnlyList<ConnectionSnapshot> GetActiveConnections();
}

/// <summary>Machine-wide throughput, used for the live strip on the dashboard.</summary>
public interface IThroughputSource
{
    /// <summary>Bytes/second since the previous call. First call returns zero.</summary>
    (double DownloadBytesPerSecond, double UploadBytesPerSecond) SampleRate();
}
