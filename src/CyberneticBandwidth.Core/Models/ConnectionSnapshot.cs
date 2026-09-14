using System.Net;
using System.Net.NetworkInformation;

namespace CyberneticBandwidth.Core.Models;

/// <summary>Identity of a socket, stable enough to diff two polls against each other.</summary>
public readonly record struct ConnectionKey(int Pid, byte Protocol, string Local, string Remote);

public sealed record ConnectionSnapshot(
    int Pid,
    string ProcessName,
    string? ExecutablePath,
    byte Protocol,
    IPEndPoint Local,
    IPEndPoint? Remote,
    TcpState State)
{
    public ConnectionKey Key => new(Pid, Protocol, Local.ToString(), Remote?.ToString() ?? string.Empty);
}

/// <summary>A socket opening or closing — what the logs view shows.</summary>
public sealed record ConnectionEventRecord(
    DateTimeOffset ObservedUtc,
    ConnectionEventKind Kind,
    int Pid,
    string ProcessName,
    byte Protocol,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    TcpState State);
