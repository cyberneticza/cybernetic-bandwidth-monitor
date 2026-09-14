using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Services;

/// <summary>
/// One-way channel from the background sampler to whatever is on screen. The sampler knows
/// nothing about WPF; the view models subscribe here and the broker marshals to the UI thread.
/// </summary>
public sealed class LiveActivityBroker
{
    private SynchronizationContext? _uiContext;

    public event Action<IReadOnlyList<UsageSample>>? UsageUpdated;
    public event Action<IReadOnlyList<ConnectionSnapshot>>? ConnectionsUpdated;
    public event Action<IReadOnlyList<ConnectionEventRecord>>? ConnectionEventsRecorded;
    public event Action<double, double>? ThroughputUpdated;
    public event Action<string?>? StatusChanged;
    public event Action<NetworkSnapshot>? NetworkChanged;
    public event Action<HealthSample>? HealthUpdated;
    public event Action<NetworkEventRecord>? NetworkEventRecorded;

    /// <summary>Called once from App startup with the dispatcher's context.</summary>
    public void AttachUiContext(SynchronizationContext context) => _uiContext = context;

    public string? StatusMessage { get; private set; }

    /// <summary>
    /// The latest of each, for view models created after the first publish; the Network page is
    /// only built when first opened, long after the watcher's startup snapshot went out.
    /// </summary>
    public NetworkSnapshot? LatestNetwork { get; private set; }

    public HealthSample? LatestHealth { get; private set; }

    public void PublishNetwork(NetworkSnapshot snapshot)
    {
        LatestNetwork = snapshot;
        Post(() => NetworkChanged?.Invoke(snapshot));
    }

    public void PublishHealth(HealthSample sample)
    {
        LatestHealth = sample;
        Post(() => HealthUpdated?.Invoke(sample));
    }

    public void PublishNetworkEvent(NetworkEventRecord record) => Post(() => NetworkEventRecorded?.Invoke(record));

    public void PublishUsage(IReadOnlyList<UsageSample> samples) => Post(() => UsageUpdated?.Invoke(samples));

    public void PublishConnections(IReadOnlyList<ConnectionSnapshot> connections) =>
        Post(() => ConnectionsUpdated?.Invoke(connections));

    public void PublishConnectionEvents(IReadOnlyList<ConnectionEventRecord> events)
    {
        if (events.Count == 0) return;
        Post(() => ConnectionEventsRecorded?.Invoke(events));
    }

    public void PublishThroughput(double down, double up) => Post(() => ThroughputUpdated?.Invoke(down, up));

    /// <summary>Null clears the banner. Anything else is shown to the user verbatim.</summary>
    public void PublishStatus(string? message)
    {
        StatusMessage = message;
        Post(() => StatusChanged?.Invoke(message));
    }

    private void Post(Action action)
    {
        if (_uiContext is null) action();
        else _uiContext.Post(static state => ((Action)state!)(), action);
    }
}
