namespace NetTrace.Core.Services;

public sealed class SamplerOptions
{
    public const string SectionName = "Sampler";

    /// <summary>How often the socket table is polled. Cheap (about a millisecond).</summary>
    public TimeSpan ConnectionInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often NDU is read. Do not go below a minute — that is its real resolution.</summary>
    public TimeSpan UsageInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Live throughput strip refresh.</summary>
    public TimeSpan ThroughputInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far back each usage pass re-reads. NDU back-fills late, and our writes replace rather
    /// than accumulate, so overlapping re-reads are free correctness.
    /// </summary>
    public TimeSpan UsageOverlap { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Days of history pulled from NDU on first run, at hour granularity.</summary>
    public int BackfillDays { get; set; } = 14;

    /// <summary>Multiplier applied to every interval once the machine looks idle.</summary>
    public int IdleBackoffFactor { get; set; } = 4;

    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int MinuteBucketDays { get; set; } = 7;
    public int HourBucketDays { get; set; } = 120;
    public int DayBucketDays { get; set; } = 1095;
    public int ConnectionEventDays { get; set; } = 14;

    /// <summary>One row every health interval; a month of them is about 90,000 rows.</summary>
    public int HealthSampleDays { get; set; } = 30;

    public int NetworkEventDays { get; set; } = 365;
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromHours(1);
}
