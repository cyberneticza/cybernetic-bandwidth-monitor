using Microsoft.Extensions.Configuration;
using Velopack;
using Velopack.Locators;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Updating a copy installed by Setup.exe (see <see cref="Installer"/>) from inside the app. Needs
/// <c>Updates:Source</c> in appsettings.json: a folder or web address holding the releases
/// <c>build\make-installer.ps1</c> writes. Without it, running a newer Setup.exe is how to update.
/// </summary>
public sealed class AppUpdates
{
    public const string SectionName = "Updates";

    private readonly UpdateManager? _manager;

    public AppUpdates(IConfiguration configuration)
    {
        Source = configuration.GetSection(SectionName)["Source"] is { Length: > 0 } source ? source.Trim() : null;

        try
        {
            IsInstalled = VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;
        }
        catch (Exception)
        {
            IsInstalled = false;
        }

        if (IsInstalled && Source is not null) _manager = new UpdateManager(Source);
    }

    /// <summary>True when this copy was installed by Setup.exe, as opposed to a build, the portable exe or the install script.</summary>
    public bool IsInstalled { get; }

    public string? Source { get; }

    public bool CanCheck => _manager is not null;

    /// <summary>The newer release, or null when this is the latest.</summary>
    public async Task<UpdateInfo?> CheckAsync() =>
        _manager is null ? null : await _manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, Action<int> progress) =>
        _manager is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync(update, progress);

    /// <summary>
    /// Hands the downloaded release to Velopack's updater, which waits for this program to exit,
    /// installs it and starts the new version. The caller shuts the app down next.
    /// </summary>
    public void ApplyAfterExit(UpdateInfo update) =>
        _manager?.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);
}
