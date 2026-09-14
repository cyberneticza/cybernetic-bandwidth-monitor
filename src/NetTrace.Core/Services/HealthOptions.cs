namespace NetTrace.Core.Services;

public sealed class HealthOptions
{
    public const string SectionName = "Health";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A tiny HTTPS response from a global CDN edge. HTTPS rather than ping because ICMP is
    /// commonly blocked on corporate and hotel networks, and because it measures the path web
    /// traffic actually takes, proxies included.
    /// </summary>
    public string InternetProbeUrl { get; set; } = "https://www.cloudflare.com/cdn-cgi/trace";

    public int ProbesPerCheck { get; set; } = 3;

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// HTTPS round trips include TLS and any proxy on the way, so they run well above ping
    /// times; above this, pages start to feel sluggish.
    /// </summary>
    public double SlowLatencyMs { get; set; } = 200;

    /// <summary>Beyond this, voice and video calls start to break up.</summary>
    public double SlowJitterMs { get; set; } = 50;

    /// <summary>Checks in a row that must agree before a change of state becomes an event.</summary>
    public int ConfirmChecks { get; set; } = 2;
}

/// <summary>Switches the user controls from Settings, which Core cannot read directly.</summary>
public sealed class MonitoringSettings
{
    private bool _healthChecksEnabled = true;

    public bool HealthChecksEnabled
    {
        get => _healthChecksEnabled;
        set
        {
            if (_healthChecksEnabled == value) return;
            _healthChecksEnabled = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when a switch changes, so a window can pass it on to a background service.</summary>
    public event Action? Changed;
}

/// <summary>Who does the collecting in this process.</summary>
public enum CollectorMode
{
    /// <summary>The usual app: the window and the collector in one process, data in the user's profile.</summary>
    InProcess,

    /// <summary>The Windows service: collects with no window and serves live figures to windows over a pipe.</summary>
    Service,

    /// <summary>A window while the service is installed: collects nothing, shows the service's data and live figures.</summary>
    Viewer
}
