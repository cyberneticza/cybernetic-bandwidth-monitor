using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NetTrace.Core.Models;

namespace NetTrace.Core.Interop;

/// <summary>
/// Connection-table access via IP Helper. This is the part of the design that works for a
/// standard user: GetExtendedTcpTable/GetExtendedUdpTable give you PID and endpoints with no
/// privilege at all. What they do NOT give you is byte counts — that comes from NDU.
/// </summary>
internal static unsafe partial class IpHlpApi
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private enum TcpTableClass { OwnerPidAll = 5 }
    private enum UdpTableClass { OwnerPid = 1 }

    private enum TableKind { Tcp4, Tcp6, Udp4, Udp6 }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow4
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow6
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow4
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow6
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, TcpTableClass tableClass, int reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, UdpTableClass tableClass, int reserved);

    /// <summary>Callback receives fully materialised managed values — no pointers escape.</summary>
    internal delegate void RowCallback(int pid, byte protocol, IPEndPoint local, IPEndPoint? remote, TcpState state);

    public static void EnumerateTcp4(RowCallback onRow)
    {
        byte[] table = ReadTable(TableKind.Tcp4);
        fixed (byte* p = table)
        {
            uint count = *(uint*)p;
            var rows = (TcpRow4*)(p + 4);
            for (uint i = 0; i < count; i++)
            {
                ref TcpRow4 r = ref rows[i];
                var local = new IPEndPoint(new IPAddress(r.LocalAddr), Port(r.LocalPort));
                IPEndPoint? remote = r.State == (uint)TcpState.Listen
                    ? null
                    : new IPEndPoint(new IPAddress(r.RemoteAddr), Port(r.RemotePort));
                onRow((int)r.OwningPid, Protocols.Tcp, local, remote, (TcpState)r.State);
            }
        }
    }

    public static void EnumerateTcp6(RowCallback onRow)
    {
        byte[] table = ReadTable(TableKind.Tcp6);
        fixed (byte* p = table)
        {
            uint count = *(uint*)p;
            var rows = (TcpRow6*)(p + 4);
            for (uint i = 0; i < count; i++)
            {
                TcpRow6* r = &rows[i];
                var local = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(r->LocalAddr, 16), r->LocalScopeId), Port(r->LocalPort));
                IPEndPoint? remote = r->State == (uint)TcpState.Listen
                    ? null
                    : new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(r->RemoteAddr, 16), r->RemoteScopeId), Port(r->RemotePort));
                onRow((int)r->OwningPid, Protocols.Tcp, local, remote, (TcpState)r->State);
            }
        }
    }

    public static void EnumerateUdp4(RowCallback onRow)
    {
        byte[] table = ReadTable(TableKind.Udp4);
        fixed (byte* p = table)
        {
            uint count = *(uint*)p;
            var rows = (UdpRow4*)(p + 4);
            for (uint i = 0; i < count; i++)
            {
                ref UdpRow4 r = ref rows[i];
                var local = new IPEndPoint(new IPAddress(r.LocalAddr), Port(r.LocalPort));
                // UDP is connectionless: the table has no peer, so remote stays null.
                onRow((int)r.OwningPid, Protocols.Udp, local, null, TcpState.Unknown);
            }
        }
    }

    public static void EnumerateUdp6(RowCallback onRow)
    {
        byte[] table = ReadTable(TableKind.Udp6);
        fixed (byte* p = table)
        {
            uint count = *(uint*)p;
            var rows = (UdpRow6*)(p + 4);
            for (uint i = 0; i < count; i++)
            {
                UdpRow6* r = &rows[i];
                var local = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(r->LocalAddr, 16), r->LocalScopeId), Port(r->LocalPort));
                onRow((int)r->OwningPid, Protocols.Udp, local, null, TcpState.Unknown);
            }
        }
    }

    private static uint Fetch(TableKind kind, IntPtr buffer, ref int size) => kind switch
    {
        TableKind.Tcp4 => GetExtendedTcpTable(buffer, ref size, false, AF_INET, TcpTableClass.OwnerPidAll, 0),
        TableKind.Tcp6 => GetExtendedTcpTable(buffer, ref size, false, AF_INET6, TcpTableClass.OwnerPidAll, 0),
        TableKind.Udp4 => GetExtendedUdpTable(buffer, ref size, false, AF_INET, UdpTableClass.OwnerPid, 0),
        TableKind.Udp6 => GetExtendedUdpTable(buffer, ref size, false, AF_INET6, UdpTableClass.OwnerPid, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Sizing call, then real call. The table can grow between the two (a process opened a socket
    /// in the gap), so retry rather than throw — this is the normal case on a busy machine.
    /// </summary>
    private static byte[] ReadTable(TableKind kind)
    {
        int size = 0;
        Fetch(kind, IntPtr.Zero, ref size);
        if (size <= 0) size = 8192;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int capacity = size;
                uint rc = Fetch(kind, buffer, ref capacity);

                if (rc == ERROR_INSUFFICIENT_BUFFER)
                {
                    size = capacity > size ? capacity : size * 2;
                    continue;
                }
                if (rc != NO_ERROR) throw new Win32Exception((int)rc, $"Reading the {kind} table failed.");

                var managed = new byte[size];
                Marshal.Copy(buffer, managed, 0, size);
                return managed;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException($"The {kind} table kept growing; gave up after 6 attempts.");
    }

    /// <summary>
    /// Ports arrive as a DWORD holding a network-order USHORT in the low 16 bits.
    /// Swapping the two bytes explicitly keeps this correct regardless of host endianness.
    /// </summary>
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
