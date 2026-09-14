using NetTrace.Core.Models;

namespace NetTrace.Core.Abstractions;

/// <summary>
/// A source of per-application byte counts.
/// The whole point of this seam is that the no-admin source (NDU) may be unavailable or
/// blocked on some machines, and an elevated source (ETW) may be available on others.
/// Nothing above this interface should care which one is running.
/// </summary>
public interface INetworkUsageSource
{
    string Name { get; }

    /// <summary>Higher wins when several sources are available. NDU = 100, ETW = 200.</summary>
    int Priority { get; }

    ValueTask<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Per-app totals for aligned buckets covering [from, to).
    /// Implementations MUST be idempotent: asking twice for the same bucket returns the same
    /// absolute number, not an increment. The persistence layer upserts on that assumption.
    /// </summary>
    ValueTask<IReadOnlyList<UsageSample>> GetUsageAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Granularity granularity,
        CancellationToken ct);
}
