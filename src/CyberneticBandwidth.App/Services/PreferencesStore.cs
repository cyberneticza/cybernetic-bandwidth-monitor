using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.Core.Services;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App.Services;

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>What the user chose in the UI, as opposed to how the collector is configured.</summary>
public sealed class UserPreferences
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>The half-minute router and internet checks behind connection quality.</summary>
    public bool HealthChecksEnabled { get; set; } = true;

    public bool ShowConnectionsHelp { get; set; } = true;

    /// <summary>Tray notification when one application sustains heavy use.</summary>
    public bool HogAlertsEnabled { get; set; } = true;

    /// <summary>Megabytes a minute, held for <see cref="UsageAlertService.SustainedMinutes"/> minutes.</summary>
    public int HogThresholdMegabytesPerMinute { get; set; } = 100;

    /// <summary>Null means no budget.</summary>
    public double? DailyBudgetGigabytes { get; set; }

    public double? MonthlyBudgetGigabytes { get; set; }

    /// <summary>Day of the month the monthly budget starts again, matching a billing cycle.</summary>
    public int BudgetResetDay { get; set; } = 1;

    /// <summary>Which budget warnings have already been shown, so a restart does not repeat them.</summary>
    public string? DailyBudgetAlerted { get; set; }

    public string? MonthlyBudgetAlerted { get; set; }
}

/// <summary>
/// preferences.json in the data folder. appsettings.json sits next to the exe, which an update
/// overwrites and a per-user install may not be able to write back to; this file is the user's.
/// </summary>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<PreferencesStore> _log;

    public UserPreferences Current { get; }

    public PreferencesStore(IOptions<StorageOptions> storage, RunMode mode, ILogger<PreferencesStore> log)
    {
        // Preferences are one person's, so they stay in the user's profile even while the data
        // itself lives in the service's shared folder.
        string folder = mode.Mode == CollectorMode.Viewer ? StorageOptions.UserDirectory : storage.Value.DataDirectory;
        _path = Path.Combine(folder, "preferences.json");
        _log = log;
        Current = Load();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save preferences to {Path}.", _path);
        }
    }

    private UserPreferences Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception ex)
        {
            // A hand-edited or half-written file must not stop the app from starting.
            _log.LogWarning(ex, "Ignoring unreadable preferences file {Path}.", _path);
        }
        return new();
    }
}
