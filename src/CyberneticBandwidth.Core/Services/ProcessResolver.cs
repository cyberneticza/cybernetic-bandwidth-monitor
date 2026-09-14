using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using CyberneticBandwidth.Core.Abstractions;

namespace CyberneticBandwidth.Core.Services;

/// <summary>
/// Caches PID lookups. The cache is keyed on PID *and* process start time, because Windows
/// recycles PIDs aggressively and a stale entry would silently attribute one app's traffic
/// to another.
///
/// Unelevated, some processes cannot be opened at all (antivirus, protected services, anything
/// running as another user), so neither their start time nor their path can be read. Their name
/// still can, from the system's process snapshot, and a socket owned by one of them is shown
/// under that name rather than being allowed to fail the whole connection table.
/// </summary>
public sealed class ProcessResolver : IProcessResolver
{
    private readonly ConcurrentDictionary<int, Entry> _cache = new();

    /// <summary>StartTicks is null for processes this user may not open.</summary>
    private readonly record struct Entry(long? StartTicks, string Name, string? Path);

    public (string Name, string? Path) Resolve(int pid)
    {
        // Windows hands a TCP connection to PID 0 once its program has let go of it and it is only
        // waiting out TimeWait; it is not the Idle process doing networking.
        if (pid == 0) return ("Closing connection", null);
        if (pid == 4) return ("System", null);

        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            long? startTicks = TryGetStartTicks(process);

            // Without a start time, a matching name is the best available check against a
            // recycled PID; a different process reusing the PID almost always has another name.
            if (_cache.TryGetValue(pid, out var cached) && cached.StartTicks == startTicks && cached.Name == name)
                return (cached.Name, cached.Path);

            string? path = startTicks is null ? null : TryGetPath(process);

            var entry = new Entry(startTicks, name, path);
            _cache[pid] = entry;
            return (entry.Name, entry.Path);
        }
        catch (ArgumentException)
        {
            // The socket outlived the process by a few milliseconds.
            _cache.TryRemove(pid, out _);
            return ($"Exited process {pid}", null);
        }
        catch (InvalidOperationException)
        {
            return ($"Exited process {pid}", null);
        }
    }

    private static long? TryGetStartTicks(Process process)
    {
        try
        {
            return process.StartTime.Ticks;
        }
        catch (Win32Exception)
        {
            // Access denied: the process exists but this user may not open it.
            return null;
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Expected for protected and cross-bitness processes when running unelevated.
            // The name is still useful, so carry on with a null path.
            return null;
        }
    }
}
