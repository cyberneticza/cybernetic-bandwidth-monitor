namespace NetTrace.Core.Models;

public readonly record struct UsagePoint(DateTimeOffset BucketStartUtc, long BytesSent, long BytesReceived)
{
    public long For(Direction direction) => direction switch
    {
        Direction.Upload => BytesSent,
        Direction.Download => BytesReceived,
        _ => BytesSent + BytesReceived
    };
}

public sealed record UsageSeries(long AppId, string DisplayName, IReadOnlyList<UsagePoint> Points);
