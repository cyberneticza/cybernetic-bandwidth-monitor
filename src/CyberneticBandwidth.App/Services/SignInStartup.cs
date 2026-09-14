using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// "Start Cybernetic Bandwidth Monitor when I sign in", as a Task Scheduler task for the signed-in user. Unlike the
/// Run registry key, a task starts promptly (Windows holds Run-key apps back after sign-in) and is
/// not switched off by Task Manager's startup-app list. Creating a task for your own account needs
/// no administrator rights. Where Task Scheduler is unavailable, the Run key is the fallback.
/// </summary>
public static class SignInStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CyberneticBandwidth";

    private static string TaskName => $"Cybernetic Bandwidth Monitor at sign-in ({Environment.UserName})";

    public static bool IsEnabled() => TaskExists() || RunKeyExists();

    /// <summary>Returns an error to show, or null when it worked (falling back to the Run key counts as working).</summary>
    public static string? Enable()
    {
        string program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CyberneticBandwidth.exe");

        if (TryCreateTask(program, out string? taskError))
        {
            DeleteRunKey();
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(RunValue, $"\"{program}\" --minimised");
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not set up start at sign-in: {taskError ?? ex.Message}";
        }
    }

    public static void Disable()
    {
        if (TaskExists()) Schtasks("/Delete", "/TN", TaskName, "/F");
        DeleteRunKey();
    }

    /// <summary>
    /// Run at every start. Moves a Run-key entry made by an older version of this copy over to a
    /// task, and points a task whose program is gone at this copy, so moving Cybernetic Bandwidth Monitor does not leave
    /// sign-in starting a missing file. Entries belonging to another copy that still exists are left alone.
    /// </summary>
    public static void RefreshIfEnabled()
    {
        string program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CyberneticBandwidth.exe");

        AdoptLegacyEntries(program);

        if (TaskExists())
        {
            string? configured = TaskProgram();
            if (configured is not null && !File.Exists(configured)) TryCreateTask(program, out _);
            return;
        }

        string? runKeyProgram = RunKeyProgram();
        if (runKeyProgram is not null && (SamePath(runKeyProgram, program) || !File.Exists(runKeyProgram))
            && TryCreateTask(program, out _))
        {
            DeleteRunKey();
        }
    }

    /// <summary>
    /// A sign-in entry made under the old name (<see cref="LegacyNetTrace"/>) starts a NetTrace that
    /// the rename replaced. It is removed, and start at sign-in stays on, now for this copy.
    /// </summary>
    private static void AdoptLegacyEntries(string program)
    {
        bool legacyTask;
        try
        {
            legacyTask = Schtasks("/Query", "/TN", LegacyNetTrace.TaskName).Code == 0;
        }
        catch (Exception)
        {
            legacyTask = false;
        }

        bool legacyRunKey;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            legacyRunKey = key?.GetValue(LegacyNetTrace.RunValue) is not null;

        if (!legacyTask && !legacyRunKey) return;

        if (!IsEnabled() && Enable() is not null) return;

        try
        {
            if (legacyTask) Schtasks("/Delete", "/TN", LegacyNetTrace.TaskName, "/F");
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(LegacyNetTrace.RunValue, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Left in place: it starts nothing once the old program is gone.
        }
    }

    private static string? TaskProgram()
    {
        try
        {
            var (code, xml) = Schtasks("/Query", "/TN", TaskName, "/XML");
            if (code != 0) return null;
            var match = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>");
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim('"') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? RunKeyProgram()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is not string command) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateTask(string program, out string? error)
    {
        error = null;
        string user = WindowsIdentity.GetCurrent().Name;
        string file = Path.Combine(Path.GetTempPath(), $"cyberneticbandwidth-signin-{Guid.NewGuid():N}.xml");

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Cybernetic Bandwidth Monitor</Author>
                <Description>Starts Cybernetic Bandwidth Monitor in the notification area when {Escape(user)} signs in. Change it in Cybernetic Bandwidth Monitor's Settings.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(program)}</Command>
                  <Arguments>--minimised</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(program)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output) = Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            if (code == 0) return true;
            error = output;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static bool TaskExists()
    {
        try
        {
            return Schtasks("/Query", "/TN", TaskName).Code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private static void DeleteRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static (int Code, string Output) Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
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
        return (process.ExitCode, output.Trim());
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
