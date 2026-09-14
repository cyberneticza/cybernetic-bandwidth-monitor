using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.Core.Services;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App.Services;

/// <summary>How this process was started; see <see cref="CollectorMode"/>.</summary>
public sealed record RunMode(CollectorMode Mode)
{
    public bool IsViewer => Mode == CollectorMode.Viewer;

    /// <summary>
    /// A window showing the service's data logs to its own user's folder: the shared logs folder
    /// belongs to the service alone (see <see cref="CollectorServiceSetup"/>).
    /// </summary>
    public string LogDirectory(StorageOptions storage) =>
        IsViewer ? System.IO.Path.Combine(StorageOptions.UserDirectory, "logs") : storage.LogDirectory;
}

/// <summary>
/// The service's own switches, in the shared folder: the service has no user profile to keep
/// preferences in, and a window can change them over the collector link.
/// </summary>
public sealed class ServiceSettingsStore(ILogger<ServiceSettingsStore> log)
{
    private sealed record Settings(bool HealthChecksEnabled);

    private static string Path => System.IO.Path.Combine(StorageOptions.SharedDirectory, "service.json");

    public bool LoadHealthChecksEnabled()
    {
        try
        {
            if (File.Exists(Path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path))?.HealthChecksEnabled ?? true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ignoring unreadable service settings in {Path}.", Path);
        }
        return true;
    }

    public void SaveHealthChecksEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(StorageOptions.SharedDirectory);
            File.WriteAllText(Path, JsonSerializer.Serialize(new Settings(enabled)));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not save service settings to {Path}.", Path);
        }
    }
}
