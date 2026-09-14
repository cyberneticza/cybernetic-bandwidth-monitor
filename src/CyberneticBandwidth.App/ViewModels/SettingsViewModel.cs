using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Services;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopes;
    private readonly StorageOptions _storage;
    private readonly RunMode _mode;
    private readonly CollectorPipeClient? _link;
    private readonly string _appVersion;
    private bool _applyingSignIn;
    private readonly ThemeService _theme;
    private readonly PreferencesStore _preferences;
    private readonly MonitoringSettings _monitoring;
    private readonly UsageAlertService _alerts;
    private readonly ILogger<SettingsViewModel> _log;

    [ObservableProperty] private string _databaseSize = "—";
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string? _signInError;

    // The Windows service
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallService), nameof(CanRemoveService), nameof(CanStartService), nameof(CanUpdateService),
        nameof(IsServiceGood), nameof(IsServiceWarning))]
    private ServiceInfo _service = new(ServiceState.NotInstalled, null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallService), nameof(CanRemoveService), nameof(CanStartService), nameof(CanUpdateService))]
    private bool _isServiceBusy;

    [ObservableProperty] private string _serviceStatusText = string.Empty;
    [ObservableProperty] private string _serviceStatusDetail = string.Empty;
    [ObservableProperty] private string? _serviceProgress;
    [ObservableProperty] private string? _serviceError;
    [ObservableProperty] private ThemePreference _themePreference;
    [ObservableProperty] private string _themeSummary = string.Empty;

    [ObservableProperty] private bool _healthChecksEnabled;
    [ObservableProperty] private bool _hogAlertsEnabled;
    [ObservableProperty] private int _hogThreshold;
    [ObservableProperty] private string _dailyBudget = string.Empty;
    [ObservableProperty] private string _monthlyBudget = string.Empty;
    [ObservableProperty] private int _budgetResetDay;
    [ObservableProperty] private string? _budgetError;

    public IReadOnlyList<int> HogThresholds { get; } = [25, 50, 100, 250, 500];

    public IReadOnlyList<int> ResetDays { get; } = Enumerable.Range(1, 28).ToList();

    public string DataDirectory { get; }

    public string SharedDirectory => StorageOptions.SharedDirectory;

    public bool CanInstallService => Service.State == ServiceState.NotInstalled && !IsServiceBusy;
    public bool CanRemoveService => Service.State != ServiceState.NotInstalled && !IsServiceBusy;
    public bool CanStartService => Service.State == ServiceState.Stopped && !IsServiceBusy;
    public bool CanUpdateService => Service.State != ServiceState.NotInstalled && !IsServiceBusy
                                    && Service.InstalledVersion is { } v && v != _appVersion;

    public bool IsServiceGood => Service.State == ServiceState.Running && (_link?.IsConnected ?? true);
    public bool IsServiceWarning => Service.State is ServiceState.Stopped or ServiceState.Unknown
                                    || (Service.State == ServiceState.Running && _link is { IsConnected: false });
    public string SamplingSummary { get; }
    public string RetentionSummary { get; }
    public string SourceSummary { get; }

    public SettingsViewModel(
        IServiceScopeFactory scopes,
        IOptions<StorageOptions> storage,
        IOptions<SamplerOptions> sampler,
        IOptions<RetentionOptions> retention,
        IEnumerable<INetworkUsageSource> sources,
        LiveActivityBroker broker,
        ThemeService theme,
        PreferencesStore preferences,
        MonitoringSettings monitoring,
        UsageAlertService alerts,
        RunMode mode,
        IServiceProvider services,
        ILogger<SettingsViewModel> log)
    {
        _scopes = scopes;
        _storage = storage.Value;
        _mode = mode;
        _link = services.GetService<CollectorPipeClient>();
        _appVersion = CollectorServiceSetup.ProgramVersion(Environment.ProcessPath) ?? "unknown";
        _theme = theme;
        _preferences = preferences;
        _monitoring = monitoring;
        _alerts = alerts;
        _log = log;

        var prefs = preferences.Current;
        _healthChecksEnabled = prefs.HealthChecksEnabled;
        _hogAlertsEnabled = prefs.HogAlertsEnabled;
        _hogThreshold = HogThresholds.Contains(prefs.HogThresholdMegabytesPerMinute) ? prefs.HogThresholdMegabytesPerMinute : 100;
        _dailyBudget = FormatGigabytes(prefs.DailyBudgetGigabytes);
        _monthlyBudget = FormatGigabytes(prefs.MonthlyBudgetGigabytes);
        _budgetResetDay = Math.Clamp(prefs.BudgetResetDay, 1, 28);

        _themePreference = theme.Preference;
        UpdateThemeSummary();
        // The rail toggle and Windows itself can both change the theme behind this page's back.
        theme.ThemeChanged += () =>
        {
            ThemePreference = theme.Preference;
            UpdateThemeSummary();
        };

        DataDirectory = storage.Value.DataDirectory;

        var s = sampler.Value;
        SamplingSummary =
            $"Sockets every {s.ConnectionInterval.TotalSeconds:0}s, " +
            $"usage every {s.UsageInterval.TotalSeconds:0}s, " +
            $"throughput every {s.ThroughputInterval.TotalSeconds:0}s. " +
            $"Intervals stretch {s.IdleBackoffFactor}x after {s.IdleAfter.TotalMinutes:0} minutes idle.";

        var r = retention.Value;
        RetentionSummary =
            $"Minute detail for {r.MinuteBucketDays} days, hourly for {r.HourBucketDays} days, " +
            $"daily for {r.DayBucketDays / 365} years. Connection log kept {r.ConnectionEventDays} days.";

        var preferred = sources.OrderByDescending(x => x.Priority).FirstOrDefault();
        SourceSummary = broker.StatusMessage
            ?? (preferred is null
                ? "No per-application byte source is registered."
                : $"Reading per-application byte counts from {preferred.Name}.");

        _ = LoadSignInAsync();
        if (_link is not null)
            _link.ConnectionChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshServiceStatus);
        RefreshServiceStatus();
        _ = RefreshAsync();
    }

    // ================================================================ start at sign-in

    private async Task LoadSignInAsync()
    {
        bool enabled = await Task.Run(SignInStartup.IsEnabled);
        _applyingSignIn = true;
        StartWithWindows = enabled;
        _applyingSignIn = false;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_applyingSignIn) return;
        _ = ApplySignInAsync(value);
    }

    private async Task ApplySignInAsync(bool enabled)
    {
        SignInError = null;
        try
        {
            string? error = await Task.Run(() =>
            {
                if (enabled) return SignInStartup.Enable();
                SignInStartup.Disable();
                return null;
            });
            SignInError = error;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not change start at sign-in.");
            SignInError = $"Could not change start at sign-in: {ex.Message}";
        }
        await LoadSignInAsync();
    }

    // ================================================================ the Windows service

    public void RefreshServiceStatus()
    {
        Service = CollectorServiceSetup.Query();
        bool connected = _link?.IsConnected ?? false;

        (ServiceStatusText, ServiceStatusDetail) = Service.State switch
        {
            ServiceState.NotInstalled => ("Not installed",
                "Cybernetic Bandwidth Monitor records while this window or its notification-area icon is running."),
            ServiceState.Running when CanUpdateService => ($"Running version {Service.InstalledVersion}; this window is {_appVersion}",
                "Update the service so both run the same version."),
            ServiceState.Running when _link is not null && !connected => ("Running, not answering yet",
                "The window reconnects on its own within a few seconds."),
            ServiceState.Running => ($"Running · version {Service.InstalledVersion}",
                $"Recording from Windows startup, whether or not anyone is signed in. Data is in {StorageOptions.SharedDirectory}."),
            ServiceState.Starting => ("Starting…", string.Empty),
            ServiceState.Stopping => ("Stopping…", string.Empty),
            ServiceState.Stopped => ("Installed but stopped",
                "Nothing is being recorded. Start it, or remove it to go back to recording in this window."),
            _ => ("State unknown", "Windows did not report the service's state.")
        };
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        if (!await RunSetupAsync(CollectorServiceSetup.InstallArgument)) return;

        try
        {
            if (!_mode.IsViewer)
            {
                ServiceProgress = "Copying your history to the shared folder…";
                string shared = Path.Combine(StorageOptions.SharedDirectory, "cyberneticbandwidth.db");
                bool copied = await CollectorServiceSetup.CopyHistoryAsync(_storage.DatabasePath, shared, replace: false);
                _log.LogInformation(copied ? "Copied history to {Shared}." : "History already in {Shared}; left as it is.", shared);
            }

            ServiceProgress = "Starting the service…";
            await CollectorServiceSetup.StartAsync();

            if (!_mode.IsViewer) RestartAsViewer("Restarting Cybernetic Bandwidth Monitor to show the service's data…");
            else Done();
        }
        catch (Exception ex)
        {
            Fail("The service was installed, but finishing up failed", ex);
        }
    }

    [RelayCommand]
    private async Task UpdateServiceAsync()
    {
        if (!await RunSetupAsync(CollectorServiceSetup.InstallArgument)) return;
        try
        {
            ServiceProgress = "Starting the service…";
            await CollectorServiceSetup.StartAsync();
            Done();
        }
        catch (Exception ex)
        {
            Fail("The service was updated, but did not start", ex);
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        IsServiceBusy = true;
        ServiceError = null;
        ServiceProgress = "Starting the service…";
        try
        {
            await CollectorServiceSetup.StartAsync();
            Done();
        }
        catch (Exception ex)
        {
            Fail("The service did not start", ex);
        }
    }

    [RelayCommand]
    private async Task RemoveServiceAsync()
    {
        if (!await RunSetupAsync(CollectorServiceSetup.RemoveArgument)) return;

        try
        {
            if (_mode.IsViewer)
            {
                // Everything recorded while the service ran comes back to this user's folder.
                ServiceProgress = "Copying the history back to your folder…";
                string shared = Path.Combine(StorageOptions.SharedDirectory, "cyberneticbandwidth.db");
                await CollectorServiceSetup.CopyHistoryAsync(shared, Path.Combine(StorageOptions.UserDirectory, "cyberneticbandwidth.db"), replace: true);
                RestartAsViewer("Restarting Cybernetic Bandwidth Monitor to record in this window again…");
            }
            else
            {
                Done();
            }
        }
        catch (Exception ex)
        {
            Fail("The service was removed, but copying the history back failed", ex);
        }
    }

    /// <summary>The UAC-prompted step. False when it was cancelled or failed, with the page already saying so.</summary>
    private async Task<bool> RunSetupAsync(string argument)
    {
        IsServiceBusy = true;
        ServiceError = null;
        ServiceProgress = "Waiting for administrator approval…";

        var result = await CollectorServiceSetup.RunElevatedAsync(argument);
        if (result.Succeeded) return true;

        IsServiceBusy = false;
        ServiceProgress = null;
        ServiceError = result.Cancelled
            ? "Cancelled at the administrator prompt. Nothing was changed."
            : $"That did not work: {result.Error}";
        RefreshServiceStatus();
        return false;
    }

    private void RestartAsViewer(string message)
    {
        ServiceProgress = message;
        CollectorServiceSetup.RestartApp();
        System.Windows.Application.Current.Shutdown();
    }

    private void Done()
    {
        IsServiceBusy = false;
        ServiceProgress = null;
        RefreshServiceStatus();
    }

    private void Fail(string what, Exception ex)
    {
        _log.LogError(ex, "{What}.", what);
        IsServiceBusy = false;
        ServiceProgress = null;
        ServiceError = $"{what}: {ex.Message}";
        RefreshServiceStatus();
    }

    partial void OnHealthChecksEnabledChanged(bool value)
    {
        _monitoring.HealthChecksEnabled = value;
        SavePreferences(p => p.HealthChecksEnabled = value);
    }

    partial void OnHogAlertsEnabledChanged(bool value) => SavePreferences(p => p.HogAlertsEnabled = value);

    partial void OnHogThresholdChanged(int value) => SavePreferences(p => p.HogThresholdMegabytesPerMinute = value);

    partial void OnDailyBudgetChanged(string value) => SaveBudget(value, (p, gb) => p.DailyBudgetGigabytes = gb);

    partial void OnMonthlyBudgetChanged(string value) => SaveBudget(value, (p, gb) => p.MonthlyBudgetGigabytes = gb);

    partial void OnBudgetResetDayChanged(int value)
    {
        SavePreferences(p => p.BudgetResetDay = value);
        _ = _alerts.RefreshAsync();
    }

    private void SaveBudget(string text, Action<UserPreferences, double?> apply)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            BudgetError = null;
            SavePreferences(p => apply(p, null));
        }
        else if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double gb) && gb > 0)
        {
            BudgetError = null;
            SavePreferences(p => apply(p, gb));
        }
        else
        {
            BudgetError = $"'{text}' is not a number of gigabytes. The budget was left as it was.";
            return;
        }

        _ = _alerts.RefreshAsync();
    }

    private void SavePreferences(Action<UserPreferences> change)
    {
        change(_preferences.Current);
        _preferences.Save();
    }

    private static string FormatGigabytes(double? value) => value?.ToString("0.##") ?? string.Empty;

    partial void OnThemePreferenceChanged(ThemePreference value)
    {
        _theme.SetPreference(value);
        UpdateThemeSummary();
    }

    private void UpdateThemeSummary() => ThemeSummary = ThemePreference == ThemePreference.System
        ? $"Following the Windows app theme, which is {_theme.Current.ToString().ToLowerInvariant()} right now."
        : "Kept regardless of the Windows setting. Choose System to follow Windows again.";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        RefreshServiceStatus();
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
            DatabaseSize = ByteFormat.Size(await repo.GetDatabaseSizeBytesAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the database size.");
            DatabaseSize = "unavailable";
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    public async Task CompactNowAsync()
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
        await repo.RunRollupAsync(CancellationToken.None);
        await RefreshAsync();
    }

}
