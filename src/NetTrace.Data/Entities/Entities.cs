namespace NetTrace.Data.Entities;

/// <summary>
/// Timestamps are stored as Unix seconds (INTEGER) rather than ISO text. Bucket alignment then
/// becomes integer arithmetic — (t / 3600) * 3600 — which SQLite can index and group on without
/// parsing dates or guessing at time zones.
/// </summary>
public sealed class AppEntity
{
    public long Id { get; set; }
    public string AttributionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public long FirstSeenUnix { get; set; }
    public long LastSeenUnix { get; set; }
}

public sealed class UsageBucketEntity
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public AppEntity? App { get; set; }
    public int Granularity { get; set; }
    public long BucketStartUnix { get; set; }
    public int InterfaceKind { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
}

public sealed class ConnectionEventEntity
{
    public long Id { get; set; }
    public long ObservedUnix { get; set; }
    public int Kind { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int Protocol { get; set; }
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string? RemoteAddress { get; set; }
    public int? RemotePort { get; set; }
    public int TcpState { get; set; }
}

public sealed class WatermarkEntity
{
    public string Key { get; set; } = string.Empty;
    public long ValueUnix { get; set; }
}

public sealed class NetworkEventEntity
{
    public long Id { get; set; }
    public long ObservedUnix { get; set; }
    public int Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class HealthSampleEntity
{
    public long Id { get; set; }
    public long ObservedUnix { get; set; }
    public int Status { get; set; }
    public double? GatewayLatencyMs { get; set; }
    public double? InternetLatencyMs { get; set; }
    public double? InternetJitterMs { get; set; }
    public double InternetLossPercent { get; set; }
}

public sealed class RegionalSpeedTestEntity
{
    public long Id { get; set; }
    public long ObservedUnix { get; set; }
    public string RegionCode { get; set; } = string.Empty;
    public double? LatencyMs { get; set; }
    public double? JitterMs { get; set; }
    public double? DownloadMbps { get; set; }
    public long BytesUsed { get; set; }
    public string? NetworkName { get; set; }
    public string? Error { get; set; }
}

public sealed class SpeedTestEntity
{
    public long Id { get; set; }
    public long ObservedUnix { get; set; }
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public string Server { get; set; } = string.Empty;
    public string? PublicAddress { get; set; }
    public string? NetworkName { get; set; }
    public int Medium { get; set; }
    public long BytesUsed { get; set; }
}
