using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Cybernetic Bandwidth Monitor was called NetTrace, and a copy installed under that name keeps its
/// history, preferences and sign-in entry under it. This carries them over to the new name once, on
/// the first start of a renamed copy. Every step is best effort: what cannot be moved is left where
/// it is, and the app starts with a fresh folder rather than not starting.
/// </summary>
public static class LegacyNetTrace
{
    public const string ProgramName = "NetTrace";
    public const string ServiceName = "NetTrace";
    public const string RunValue = "NetTrace";

    public static string TaskName => $"NetTrace at sign-in ({Environment.UserName})";

    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetTrace");

    /// <summary>
    /// Moves %LocalAppData%\NetTrace to <see cref="StorageOptions.UserDirectory"/> and renames the
    /// database and logs inside it. Call before anything creates the new folder (logging does).
    /// Returns what happened, for the log once there is one, or null when there was nothing to do.
    /// </summary>
    public static string? MoveUserData()
    {
        string target = StorageOptions.UserDirectory;
        try
        {
            if (!Directory.Exists(UserDirectory)) return null;
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return $"Left the NetTrace folder {UserDirectory} alone: {target} already has data.";

            // A NetTrace still running holds the database open, and would carry on writing to a
            // folder that has moved away from under it.
            if (Process.GetProcessesByName(ProgramName).Length > 0)
                return $"NetTrace is still running, so its history in {UserDirectory} stays there for now.";

            if (Directory.Exists(target)) Directory.Delete(target);
            Directory.Move(UserDirectory, target);

            // The database and its write-ahead log belong together by name, so all three are renamed.
            foreach (string suffix in new[] { ".db", ".db-wal", ".db-shm" })
                RenameIfPresent(Path.Combine(target, "nettrace" + suffix), Path.Combine(target, "cyberneticbandwidth" + suffix));

            // Renamed so the rolling log's retention finds them and ages them out.
            string logs = Path.Combine(target, "logs");
            if (Directory.Exists(logs))
            {
                foreach (string file in Directory.EnumerateFiles(logs, "nettrace-*.log"))
                    RenameIfPresent(file, Path.Combine(logs, "cyberneticbandwidth-" + Path.GetFileName(file)["nettrace-".Length..]));
            }

            return $"Moved the history and preferences from {UserDirectory} to {target}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not move the NetTrace folder {UserDirectory} to {target}: {ex.Message}";
        }
    }

    /// <summary>
    /// The service from before the rename. It cannot be removed without administrator rights, so it
    /// is only reported: it keeps recording on its own until someone removes it.
    /// </summary>
    public static bool IsServiceInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s =>
            {
                using (s) return s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RenameIfPresent(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
    }
}
