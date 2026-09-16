using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Velopack;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// The one-click installer, built by <c>build\make-installer.ps1</c> with Velopack. Setup.exe installs
/// for the current user, with no UAC prompt, to <c>%LocalAppData%\CyberneticBandwidthMonitor</c>: the
/// program in <c>current\</c> beside Velopack's <c>Update.exe</c>. Running the Setup.exe of a newer
/// version updates that install in place.
///
/// Velopack runs this program with <c>--veloapp-*</c> arguments at install, update and uninstall;
/// <see cref="Run"/> handles those and exits before any window, host or collector starts.
/// </summary>
public static class Installer
{
    /// <summary>
    /// The Velopack package id, which names the install folder. Never <c>CyberneticBandwidth</c>:
    /// uninstalling deletes the whole install folder, and <c>%LocalAppData%\CyberneticBandwidth</c> is the history.
    /// </summary>
    public const string PackId = "CyberneticBandwidthMonitor";

    private const string ScriptInstallUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CyberneticBandwidth";

    /// <summary>Where <c>build\install-peruser.ps1</c> installs.</summary>
    public static string ScriptInstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CyberneticBandwidth");

    public static void Run() =>
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => RemoveScriptInstall())
            .OnBeforeUninstallFastCallback(_ => SignInStartup.DisableFor(AppContext.BaseDirectory))
            .Run();

    /// <summary>
    /// A copy installed by <c>install-peruser.ps1</c> is replaced by this one: stopped, and its folder,
    /// Start Menu shortcut and uninstall entry removed. A sign-in entry for it is pointed at this copy
    /// on first start, once its program is gone (<see cref="SignInStartup.RefreshIfEnabled"/>).
    /// Best effort: what cannot be removed is left, and the install carries on.
    /// </summary>
    private static void RemoveScriptInstall()
    {
        string directory = ScriptInstallDirectory;
        if (!Directory.Exists(directory)) return;

        foreach (var process in Process.GetProcessesByName("CyberneticBandwidth"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? path = process.MainModule?.FileName;
                    if (path is null || !SignInStartup.IsInside(path, directory)) continue;
                    process.Kill();
                    process.WaitForExit(5_000);
                }
                catch (Exception)
                {
                    // Gone already, or not ours to inspect.
                }
            }
        }

        try
        {
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "CyberneticBandwidth.lnk"));
            Registry.CurrentUser.DeleteSubKeyTree(ScriptInstallUninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // A stale shortcut or Apps entry; the program is still replaced.
        }

        // A killed process can hold its files for a moment.
        for (int attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
