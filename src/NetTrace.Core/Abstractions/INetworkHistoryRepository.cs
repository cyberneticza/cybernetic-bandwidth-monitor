using NetTrace.Core.Models;

namespace NetTrace.Core.Abstractions;

/// <summary>What happened to the connection itself, as opposed to who used it.</summary>
public interface INetworkHistoryRepository
{
    Task InsertNetworkEventAsync(NetworkEventRecord record, CancellationToken ct);

    /// <summary>Events in [from, to), oldest first.</summary>
    Task<IReadOnlyList<NetworkEventRecord>> GetNetworkEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task InsertHealthSampleAsync(HealthSample sample, CancellationToken ct);

    /// <summary>Samples in [from, to), oldest first.</summary>
    Task<IReadOnlyList<HealthSample>> GetHealthSamplesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task InsertSpeedTestAsync(SpeedTestResult result, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SpeedTestResult>> GetSpeedTestsAsync(int take, CancellationToken ct);

    Task InsertRegionalSpeedTestAsync(RegionalSpeedResult result, CancellationToken ct);

    /// <summary>Newest first, across all regions.</summary>
    Task<IReadOnlyList<RegionalSpeedResult>> GetRegionalSpeedTestsAsync(int take, CancellationToken ct);
}
