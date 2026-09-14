namespace CyberneticBandwidth.Core.Models;

/// <summary>
/// The statistics every latency figure uses, so a health check and a speed test mean the same
/// thing by latency and jitter.
/// </summary>
public static class RoundTrips
{
    /// <summary>Null when there are no values.</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The mean difference between consecutive round trips; null with fewer than two.</summary>
    public static double? Jitter(IReadOnlyList<double> times) =>
        times.Count < 2 ? null : times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average();
}
