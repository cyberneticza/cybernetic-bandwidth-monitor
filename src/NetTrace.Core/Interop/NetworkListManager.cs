using System.Runtime.InteropServices;

namespace NetTrace.Core.Interop;

/// <summary>
/// The names Windows gives networks ("ppc.africa", "Network 2"), by adapter. WinRT only exposes
/// the connection's own name, which for a wired link is just "Ethernet"; the Network List
/// Manager behind the Settings app has the real one. Readable by a standard user.
/// </summary>
internal static class NetworkListManager
{
    private static readonly Guid ManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNewEnum();

        [PreserveSig]
        int Next(int celt, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    }

    /// <summary>Network name per adapter id. Empty if the service is unavailable.</summary>
    public static IReadOnlyDictionary<Guid, string> GetNamesByAdapter()
    {
        var names = new Dictionary<Guid, string>();
        object? instance = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ManagerClsid, throwOnError: false);
            if (type is null) return names;

            instance = Activator.CreateInstance(type);
            if (instance is not INetworkListManager manager) return names;

            var connections = manager.GetNetworkConnections();
            while (connections.Next(1, out var connection, out int fetched) == 0 && fetched == 1)
            {
                try
                {
                    string name = connection.GetNetwork().GetName();
                    if (!string.IsNullOrWhiteSpace(name)) names[connection.GetAdapterId()] = name;
                }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                }
            }
            Marshal.ReleaseComObject(connections);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Name enrichment only; callers fall back to what WinRT reports.
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }

        return names;
    }
}
