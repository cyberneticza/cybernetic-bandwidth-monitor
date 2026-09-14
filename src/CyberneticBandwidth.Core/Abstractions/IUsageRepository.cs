using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Abstractions;

public interface IUsageRepository
{
    /// <summary>
    /// Inserts or replaces buckets. Replacing (not adding) is deliberate — see
    /// <see cref="INetworkUsageSource"/> for why samples are absolute.
    /// </summary>
    Task UpsertBucketsAsync(IReadOnlyList<UsageSample> samples, CancellationToken ct);

    /// <summary>
    /// Re-derives every stored app's display name from its attribution id, and saves the ones
    /// that changed. Returns how many were renamed.
    /// </summary>
    Task<int> RefreshAppNamesAsync(Func<string, string> resolveName, CancellationToken ct);

    /// <summary>
    /// Traffic across all applications for buckets starting in [from, to), gathered into bins of
    /// <paramref name="bin"/> aligned to the Unix epoch (so to UTC hours and days). Bins with no
    /// traffic are absent. Minute data is only kept for a week and hour data for four months, so
    /// a bin finer than what survives for that date comes back empty.
    /// </summary>
    Task<IReadOnlyList<UsagePoint>> GetTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bin, CancellationToken ct);

    /// <summary>As <see cref="GetTimelineAsync"/>, split by application.</summary>
    Task<IReadOnlyList<UsageSeries>> GetAppTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bin, CancellationToken ct);

    Task InsertConnectionEventsAsync(IReadOnlyList<ConnectionEventRecord> events, CancellationToken ct);

    Task<IReadOnlyList<ConnectionEventRecord>> GetRecentConnectionEventsAsync(int take, CancellationToken ct);

    Task<DateTimeOffset?> GetWatermarkAsync(string key, CancellationToken ct);

    Task SetWatermarkAsync(string key, DateTimeOffset value, CancellationToken ct);

    /// <summary>Promotes minute -> hour -> day and prunes anything past its retention.</summary>
    Task RunRollupAsync(CancellationToken ct);

    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct);
}
