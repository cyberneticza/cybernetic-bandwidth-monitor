using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace NetTrace.Core.Services;

/// <summary>
/// Turns a Windows attribution id into a name a person recognises.
///
/// NDU only fills in AttributionName for callers running inside an app package. Unpackaged, it
/// is always empty, but AttributionId still says who used the bytes, in one of four shapes:
///  - an NT device path to the executable: \device\harddiskvolume3\...\msedge.exe
///  - a package family name for Store and MSIX apps: MSTeams_8wekyb3d8bbwe
///  - "System" or "System\SMB" for traffic the kernel owns
///  - empty, for traffic Windows could not attribute at all
/// </summary>
public sealed partial class AppNameResolver(ILogger<AppNameResolver> log)
{
    public const string Unattributed = "Unattributed system traffic";

    private const string DevicePrefix = @"\device\";
    private const string SystemId = "System";

    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackageManager _packages = new();
    private volatile IReadOnlyList<KeyValuePair<string, string>> _devices = [];

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDevice(string deviceName, [Out] char[] targetPath, int max);

    /// <summary>Cached per id: version resources and package lookups both hit the disk.</summary>
    public string Resolve(string? attributionId)
    {
        if (string.IsNullOrWhiteSpace(attributionId)) return Unattributed;
        return _names.GetOrAdd(attributionId, ResolveUncached);
    }

    private string ResolveUncached(string id)
    {
        try
        {
            if (id.Equals(SystemId, StringComparison.OrdinalIgnoreCase)) return "Windows (System)";

            if (id.StartsWith(SystemId + @"\", StringComparison.OrdinalIgnoreCase))
                return $"Windows ({id[(SystemId.Length + 1)..]})";

            if (id.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
                return FromExecutable(id);

            if (id.Contains('_')) return FromPackage(id);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not resolve a friendly name for {AttributionId}.", id);
        }

        return id;
    }

    private string FromExecutable(string devicePath)
    {
        string? path = ToDosPath(devicePath);
        string fileName = Path.GetFileNameWithoutExtension(devicePath);

        if (path is null || !File.Exists(path)) return fileName;

        var info = FileVersionInfo.GetVersionInfo(path);

        // Description names the program ("Microsoft Edge"); product often names the suite it
        // shipped in ("Microsoft® Windows® Operating System"), so it is only the fallback.
        return FirstUseful(info.FileDescription, info.ProductName) ?? fileName;
    }

    private string FromPackage(string familyName)
    {
        var package = _packages.FindPackagesForUser(string.Empty, familyName).FirstOrDefault();
        string? display = package?.DisplayName;

        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return display;

        // Uninstalled since, or no display name: "5319275A.WhatsAppDesktop_cv1g1gvanyjgm" -> "WhatsAppDesktop".
        string name = familyName[..familyName.LastIndexOf('_')];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private string? ToDosPath(string devicePath)
    {
        var match = Match(_devices, devicePath);
        if (match is null)
        {
            // Drives come and go (USB, VHD mounts), so rebuild the map on a miss rather than once.
            _devices = BuildDeviceMap();
            match = Match(_devices, devicePath);
        }
        return match;

        static string? Match(IReadOnlyList<KeyValuePair<string, string>> devices, string path)
        {
            foreach (var (device, drive) in devices)
            {
                if (path.Length > device.Length
                    && path[device.Length] == '\\'
                    && path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                {
                    return drive + path[device.Length..];
                }
            }
            return null;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildDeviceMap()
    {
        var map = new List<KeyValuePair<string, string>>();
        var buffer = new char[1024];

        foreach (var root in Environment.GetLogicalDrives())
        {
            string drive = root.TrimEnd('\\');
            uint length = QueryDosDevice(drive, buffer, buffer.Length);
            if (length == 0) continue;

            // The buffer is a double-null-terminated list; the first entry is the live mapping.
            string device = new string(buffer, 0, (int)length).Split('\0')[0];
            if (device.Length > 0) map.Add(new(device, drive));
        }

        return map;
    }

    private static string? FirstUseful(params string?[] candidates) =>
        candidates.Select(c => c?.Trim()).FirstOrDefault(c => !string.IsNullOrEmpty(c));
}
