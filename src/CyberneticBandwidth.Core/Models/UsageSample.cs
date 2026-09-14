namespace CyberneticBandwidth.Core.Models;

/// <summary>
/// One application's traffic inside one aligned time bucket.
/// Samples carry absolute totals for the bucket, never deltas — that is what makes
/// re-reading an overlapping window safe and the whole pipeline idempotent.
/// </summary>
public readonly record struct UsageSample(
    string AttributionId,
    string DisplayName,
    DateTimeOffset BucketStartUtc,
    Granularity Granularity,
    InterfaceKind Interface,
    long BytesSent,
    long BytesReceived)
{
    public long Total => BytesSent + BytesReceived;
}
