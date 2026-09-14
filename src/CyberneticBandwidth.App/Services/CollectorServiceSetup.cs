using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Data.Sqlite;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App.Services;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Unknown
}

/// <summary>The installed service as seen from a window: its state and the version of its program.</summary>
public sealed record ServiceInfo(ServiceState State, string? InstalledVersion);

/// <summary>What came of asking for administrator approval.</summary>
public sealed record SetupResult(bool Succeeded, bool Cancelled, string? Error)
{
    public static SetupResult Ok { get; } = new(true, false, null);
}

/// <summary>
/// Installs and removes Cybernetic Bandwidth Monitor as a Windows service. This is the one part of Cybernetic Bandwidth Monitor that needs
/// administrator rights, and it only runs when a user asks for it from Settings.
///
/// A window cannot do it itself: it starts <c>CyberneticBandwidth.exe --install-service</c> (or
/// <c>--remove-service</c>) with a UAC prompt, and that elevated copy does only what needs the
/// rights. It copies the program to Program Files (a service must never run from a folder its user
/// can write to, or anything running as that user could swap the file and run as SYSTEM), lets every
/// user of the PC change the files in %ProgramData%\CyberneticBandwidth, and registers the service so that signed-in users
/// may start and stop it without another prompt. The window then moves the history across, starts
/// the service and restarts itself as a viewer.
/// </summary>
public static class CollectorServiceSetup
{
    public const string ServiceName = "CyberneticBandwidth";
    public const string InstallArgument = "--install-service";
    public const string RemoveArgument = "--remove-service";
    public const string ResultArgument = "--result";

    private const string DisplayName = "Cybernetic Bandwidth Monitor collector";
    private const string Description =
        "Records which applications use the network, connection quality and network changes for Cybernetic Bandwidth Monitor, " +
        "from Windows startup, whether or not anyone is signed in.";

    /// <summary>
    /// The default service descriptor, plus start and stop (RP, WP) for interactive users, so a
    /// window can start a stopped service without asking for administrator approval again.
    /// </summary>
    private const string ServiceSecurity =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private const int CancelledByUser = 1223;

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CyberneticBandwidth");

    public static string InstalledProgram => Path.Combine(InstallDirectory, "CyberneticBandwidth.exe");

    // ================================================================ from a window

    public static bool IsInstalled()
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

    public static ServiceInfo Query()
    {
        if (!IsInstalled()) return new ServiceInfo(ServiceState.NotInstalled, null);

        string? version = ProgramVersion(InstalledProgram);

        try
        {
            using var service = new ServiceController(ServiceName);
            var state = service.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
                _ => ServiceState.Unknown
            };
            return new ServiceInfo(state, version);
        }
        catch (Exception)
        {
            return new ServiceInfo(ServiceState.Unknown, version);
        }
    }

    /// <summary>The human version of a Cybernetic Bandwidth Monitor program ("1.2.0", "1.2.0-beta"), or null when it is not there.</summary>
    public static string? ProgramVersion(string? program) =>
        program is not null && File.Exists(program)
            ? FileVersionInfo.GetVersionInfo(program).ProductVersion?.Split('+')[0]
            : null;

    /// <summary>Starts the installed service. Allowed without elevation by the descriptor set at install.</summary>
    public static Task StartAsync() => Task.Run(() =>
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    });

    /// <summary>Runs this program elevated with <paramref name="argument"/>, behind a UAC prompt.</summary>
    public static Task<SetupResult> RunElevatedAsync(string argument) => Task.Run(() =>
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"cyberneticbandwidth-setup-{Guid.NewGuid():N}.txt");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{argument} {ResultArgument} \"{resultFile}\""
            };

            using var process = Process.Start(start);
            if (process is null) return new SetupResult(false, false, "The setup step did not start.");
            process.WaitForExit();

            string? detail = File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : null;
            return process.ExitCode == 0
                ? SetupResult.Ok
                : new SetupResult(false, false, string.IsNullOrEmpty(detail) ? $"Setup ended with code {process.ExitCode}." : detail);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledByUser)
        {
            return new SetupResult(false, true, null);
        }
        catch (Exception ex)
        {
            return new SetupResult(false, false, ex.Message);
        }
        finally
        {
            try { File.Delete(resultFile); } catch (IOException) { }
        }
    });

    /// <summary>
    /// Copies <paramref name="fromDatabase"/> into <paramref name="toDatabase"/> as a consistent
    /// snapshot, while the source may still be in use. Does nothing when the target already exists.
    /// </summary>
    public static Task<bool> CopyHistoryAsync(string fromDatabase, string toDatabase, bool replace) => Task.Run(() =>
    {
        if (!File.Exists(fromDatabase)) return false;
        if (File.Exists(toDatabase) && !replace) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(toDatabase)!);
        string staging = toDatabase + ".copying";
        if (File.Exists(staging)) File.Delete(staging);

        using (var connection = new SqliteConnection($"Data Source={fromDatabase};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", staging);
            command.ExecuteNonQuery();
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(toDatabase + suffix)) File.Delete(toDatabase + suffix);
        }
        File.Move(staging, toDatabase);
        return true;
    });

    /// <summary>Starts a fresh copy of this program once this one has exited, then lets this one close.</summary>
    public static void RestartApp()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{App.AfterArgument} {Environment.ProcessId}")
        {
            UseShellExecute = false
        });
    }

    // ================================================================ the elevated step

    public static bool IsSetupCommand(string[] args) =>
        args.Any(a => a.Equals(InstallArgument, StringComparison.OrdinalIgnoreCase)
                   || a.Equals(RemoveArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The whole elevated process: install or remove, write any error for the window, exit.</summary>
    public static int RunSetupCommand(string[] args)
    {
        string? resultFile = ArgumentAfter(args, ResultArgument);
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Installing or removing the Cybernetic Bandwidth Monitor service needs administrator approval.");

            if (args.Any(a => a.Equals(InstallArgument, StringComparison.OrdinalIgnoreCase))) Install();
            else Remove();
            return 0;
        }
        catch (Exception ex)
        {
            if (resultFile is not null)
            {
                try { File.WriteAllText(resultFile, ex.Message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return 1;
        }
    }

    private static void Install()
    {
        StopIfRunning();
        CopyProgram();
        PrepareSharedDirectory();

        string binPath = $"\"{InstalledProgram}\" {Program.ServiceArgument}";
        if (IsInstalled())
            Sc("config", ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName);
        else
            Sc("create", ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName);

        Sc("description", ServiceName, Description);
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        Sc("sdset", ServiceName, ServiceSecurity);
    }

    private static void Remove()
    {
        if (IsInstalled())
        {
            StopIfRunning();
            Sc("delete", ServiceName);
        }

        // The program may take a moment to let go of its files after stopping.
        for (int attempt = 0; attempt < 20 && Directory.Exists(InstallDirectory); attempt++)
        {
            try
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void StopIfRunning()
    {
        if (!IsInstalled()) return;
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The program files beside this exe: the single file and its settings when published, or the
    /// whole build output when run from a build. Guides, scripts and logs stay behind.
    /// </summary>
    private static void CopyProgram()
    {
        string source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (source.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(InstallDirectory);
        string[] skippedExtensions = [".pdf", ".ps1", ".log", ".db", ".db-wal", ".db-shm"];

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (skippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

            string target = Path.Combine(InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// %ProgramData%\CyberneticBandwidth. Every user of the PC may change the files in it (windows read and write
    /// the database the service fills, and a user copies their history in), but not the folder itself
    /// or its logs folder: a user who could rename either and put a junction in its place could send
    /// the service's writes, made as SYSTEM, to any file on the disk.
    /// </summary>
    private static void PrepareSharedDirectory()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var directory = OwnDirectory(StorageOptions.SharedDirectory);

        var security = directory.GetAccessControl();
        // Any user may create folders in ProgramData, so this one may have been made by one; taking
        // ownership stops its maker granting themselves back what is removed here.
        security.SetOwner(administrators);
        security.PurgeAccessRules(users);
        // The folder itself: list it and add files, nothing that renames or deletes it.
        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        // Everything in it.
        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.Modify, inherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        directory.SetAccessControl(security);

        // Only the service writes here; windows log to their own user's folder.
        var logs = OwnDirectory(Path.Combine(StorageOptions.SharedDirectory, "logs"));
        var logSecurity = new DirectorySecurity();
        logSecurity.SetOwner(administrators);
        logSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        logSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        logSecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        logSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        logs.SetAccessControl(logSecurity);
    }

    /// <summary>Creates the folder, refusing one that is a link to somewhere else.</summary>
    private static DirectoryInfo OwnDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"{path} is a link to another folder, which the service will not write through. Delete it, then install again.");
        directory.Create();
        return directory;
    }

    private static void Sc(params string[] arguments)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"sc {arguments[0]} failed ({process.ExitCode}): {output.Trim()}");
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
