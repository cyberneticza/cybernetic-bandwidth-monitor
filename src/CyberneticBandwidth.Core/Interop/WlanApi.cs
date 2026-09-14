using System.Runtime.InteropServices;

namespace CyberneticBandwidth.Core.Interop;

/// <summary>
/// Signal quality of the connected Wi-Fi network, from the Native Wifi API. Works for a
/// standard user. On Windows 11 24H2 and later the call is refused when location access for
/// desktop apps is turned off; callers treat that as "signal unknown", not as a failure.
/// </summary>
internal static unsafe partial class WlanApi
{
    private const uint ClientVersion = 2;
    private const int WlanIntfOpcodeCurrentConnection = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceInfoList
    {
        public uint NumberOfItems;
        public uint Index;
        // Followed by NumberOfItems WLAN_INTERFACE_INFO (Guid + 512 chars + state).
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfo
    {
        public Guid InterfaceGuid;
        public fixed char Description[256];
        public int State;
    }

    /// <summary>
    /// WLAN_CONNECTION_ATTRIBUTES. Sequential layout reproduces the native alignment, including
    /// the two padding bytes after the six-byte BSSID.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionAttributes
    {
        public int State;
        public int ConnectionMode;
        public fixed char ProfileName[256];
        // WLAN_ASSOCIATION_ATTRIBUTES
        public uint SsidLength;
        public fixed byte Ssid[32];
        public int BssType;
        public fixed byte Bssid[6];
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRateKbps;
        public uint TxRateKbps;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(IntPtr handle, in Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr memory);

    public readonly record struct WiFiLink(Guid InterfaceId, int SignalPercent);

    /// <summary>The connected Wi-Fi interfaces. Empty when there are none, or when Windows refuses.</summary>
    public static IReadOnlyList<WiFiLink> GetConnectedLinks()
    {
        var links = new List<WiFiLink>();
        IntPtr handle = IntPtr.Zero;

        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle) != 0) return links;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr listPtr) != 0) return links;

            try
            {
                var list = (InterfaceInfoList*)listPtr;
                var items = (InterfaceInfo*)(listPtr + sizeof(InterfaceInfoList));

                for (uint i = 0; i < list->NumberOfItems; i++)
                {
                    Guid id = items[i].InterfaceGuid;
                    if (WlanQueryInterface(handle, id, WlanIntfOpcodeCurrentConnection, IntPtr.Zero,
                            out _, out IntPtr data, IntPtr.Zero) != 0)
                        continue;

                    try
                    {
                        var attributes = (ConnectionAttributes*)data;
                        links.Add(new WiFiLink(id, (int)Math.Min(100, attributes->SignalQuality)));
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(listPtr);
            }
        }
        catch (DllNotFoundException)
        {
            // Server SKUs without the Wireless LAN service.
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        return links;
    }
}
