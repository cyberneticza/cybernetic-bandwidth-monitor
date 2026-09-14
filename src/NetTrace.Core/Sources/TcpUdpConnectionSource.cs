using Microsoft.Extensions.Logging;
using NetTrace.Core.Abstractions;
using NetTrace.Core.Interop;
using NetTrace.Core.Models;

namespace NetTrace.Core.Sources;

public sealed class TcpUdpConnectionSource(
    IProcessResolver resolver,
    ILogger<TcpUdpConnectionSource> log) : IConnectionSource
{
    public IReadOnlyList<ConnectionSnapshot> GetActiveConnections()
    {
        var list = new List<ConnectionSnapshot>(512);

        // Hundreds of sockets share a few dozen PIDs, and every lookup takes a process snapshot;
        // resolve each PID once per poll.
        var names = new Dictionary<int, (string Name, string? Path)>();

        void Add(int pid, byte protocol, System.Net.IPEndPoint local, System.Net.IPEndPoint? remote,
                 System.Net.NetworkInformation.TcpState state)
        {
            if (!names.TryGetValue(pid, out var resolved))
            {
                try
                {
                    resolved = resolver.Resolve(pid);
                }
                catch (Exception ex)
                {
                    // One unreadable process must cost one name, not the rest of the table.
                    log.LogDebug(ex, "Could not resolve process {Pid}.", pid);
                    resolved = ($"Process {pid}", null);
                }
                names[pid] = resolved;
            }

            list.Add(new ConnectionSnapshot(pid, resolved.Name, resolved.Path, protocol, local, remote, state));
        }

        Read(() => IpHlpApi.EnumerateTcp4(Add), "TCP/IPv4");
        Read(() => IpHlpApi.EnumerateTcp6(Add), "TCP/IPv6");
        Read(() => IpHlpApi.EnumerateUdp4(Add), "UDP/IPv4");
        Read(() => IpHlpApi.EnumerateUdp6(Add), "UDP/IPv6");

        return list;
    }

    /// <summary>
    /// One failing address family must not blank out the other three — IPv6 is disabled on
    /// some machines and the call simply errors.
    /// </summary>
    private void Read(Action read, string label)
    {
        try
        {
            read();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read the {Label} connection table.", label);
        }
    }
}
