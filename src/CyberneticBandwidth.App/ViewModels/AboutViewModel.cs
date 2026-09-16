using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Data;
using Velopack;

namespace CyberneticBandwidth.App.ViewModels;

public sealed record AboutDetail(string Label, string Value);

/// <summary>
/// Everything shown here is read from the running build: the version numbers come from
/// VersionPrefix in Directory.Build.props, so bumping that one value is the whole release step.
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public string ProductName { get; }
    public string Version { get; }
    public string Author { get; }
    public string Copyright { get; }
    public string Description { get; }
    public BitmapSource? Logo { get; }
    public IReadOnlyList<AboutDetail> Details { get; }

    private readonly string _dataDirectory;
    private readonly string _logDirectory;
    private readonly AppUpdates _updates;
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    private bool _isUpdateBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private string? _availableVersion;

    [ObservableProperty]
    private string _updateStatus;

    public bool CanCheckForUpdates => _updates.CanCheck;

    public bool IsUpdateAvailable => AvailableVersion is not null;

    public AboutViewModel(IOptions<StorageOptions> storage, IEnumerable<INetworkUsageSource> sources, RunMode mode, AppUpdates updates)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutViewModel).Assembly;
        var version = assembly.GetName().Version ?? new Version(0, 0, 0, 0);

        ProductName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Cybernetic Bandwidth Monitor";
        Author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Lance du Plessis";
        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        Description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty;

        // The informational version is the human one ("1.2.3", or "1.2.3-beta" for a prerelease).
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                               ?? $"{version.Major}.{version.Minor}.{version.Build}";
        Version = informational.Split('+')[0];

        _dataDirectory = storage.Value.DataDirectory;
        _logDirectory = mode.LogDirectory(storage.Value);

        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CyberneticBandwidth.exe");
        string built = File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("d MMMM yyyy, HH:mm") : "unknown";
        string fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? version.ToString();
        string source = sources.OrderByDescending(s => s.Priority).FirstOrDefault()?.Name ?? "none";

        Details =
        [
            new("Version", Version),
            new("Major", version.Major.ToString()),
            new("Minor", version.Minor.ToString()),
            new("Patch", version.Build.ToString()),
            new("File version", fileVersion),
            new("Built", built),
            new("Runtime", $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})"),
            new("Windows", WindowsName()),
            new("Byte counts from", source),
            new("Installed by", updates.IsInstalled ? "Setup.exe" : "Not the installer"),
            new("Installed in", AppContext.BaseDirectory.TrimEnd('\\')),
            new("Data folder", _dataDirectory)
        ];

        Logo = LoadLogo();

        _updates = updates;
        _updateStatus = !updates.IsInstalled
            ? "This copy was not installed with Setup.exe, so it cannot update itself. Install with CyberneticBandwidthMonitor-win-Setup.exe to get updates; your history is kept."
            : updates.CanCheck
                ? $"Updates come from {updates.Source}."
                : "To update, run the Setup.exe of a newer version. It installs over this one and keeps your history and settings.";
    }

    private bool CanCheckNow => _updates.CanCheck && !IsUpdateBusy;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckForUpdatesAsync()
    {
        IsUpdateBusy = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            _availableUpdate = await _updates.CheckAsync();
            AvailableVersion = _availableUpdate?.TargetFullRelease.Version.ToString();
            UpdateStatus = AvailableVersion is null
                ? $"You have the latest version, {Version}."
                : $"Version {AvailableVersion} is available. Installing it restarts Cybernetic Bandwidth Monitor; your history is kept.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Could not check for updates at {_updates.Source}: {ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private bool CanInstallNow => _availableUpdate is not null && !IsUpdateBusy;

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null) return;

        IsUpdateBusy = true;
        try
        {
            await _updates.DownloadAsync(_availableUpdate, percent =>
                Application.Current.Dispatcher.BeginInvoke(() => UpdateStatus = $"Downloading version {AvailableVersion}… {percent}%"));

            UpdateStatus = $"Installing version {AvailableVersion}…";
            _updates.ApplyAfterExit(_availableUpdate);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Could not install version {AvailableVersion}: {ex.Message}";
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private void CopyDetails()
    {
        var text = new StringBuilder()
            .AppendLine($"{ProductName} {Version}")
            .AppendLine($"Created by {Author}");
        foreach (var d in Details) text.AppendLine($"{d.Label}: {d.Value}");

        // Another app holding the clipboard open makes this throw; a retry by the user is fine.
        try { Clipboard.SetText(text.ToString()); }
        catch (COMException) { }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_dataDirectory);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_logDirectory);

    // Windows 11 still reports itself as 10.0; the build number is what tells them apart.
    private static string WindowsName()
    {
        int build = Environment.OSVersion.Version.Build;
        return $"Windows {(build >= 22000 ? "11" : "10")} (build {build})";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// The 256 px frame of the app icon. An Image pointed at the .ico directly takes the first
    /// frame, which is the 16 px one, and scales it up into a blur.
    /// </summary>
    private static BitmapSource? LoadLogo()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CyberneticBandwidth.ico")).Stream;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.MaxBy(f => f.PixelWidth);
        }
        catch
        {
            return null;
        }
    }
}
