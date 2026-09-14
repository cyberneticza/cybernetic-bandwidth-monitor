namespace NetTrace.Core.Models;

/// <summary>Bucket size. Values are persisted — do not renumber.</summary>
public enum Granularity
{
    Minute = 0,
    Hour = 1,
    Day = 2
}

/// <summary>Physical medium the bytes travelled over. Persisted — do not renumber.</summary>
public enum InterfaceKind
{
    Unknown = 0,
    Ethernet = 1,
    WiFi = 2,
    Cellular = 3
}

public enum Direction
{
    Both = 0,
    Download = 1,
    Upload = 2
}

public enum ConnectionEventKind
{
    Opened = 0,
    Closed = 1
}

public static class Protocols
{
    public const byte Tcp = 6;
    public const byte Udp = 17;

    public static string Name(byte protocol) => protocol switch
    {
        Tcp => "TCP",
        Udp => "UDP",
        _ => protocol.ToString()
    };
}
