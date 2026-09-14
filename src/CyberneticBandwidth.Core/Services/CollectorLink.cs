using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Services;

/// <summary>
/// The live link between the Cybernetic Bandwidth Monitor service and the windows that show its data. Stored history
/// needs no link, both sides read the same database; this carries what the window would otherwise
/// have computed itself: throughput every second, the socket list, usage samples, network changes,
/// health samples and status. One JSON object per line over a named pipe.
///
/// Only the local machine may connect (network logons are denied), and any signed-in user may,
/// which matches the shared data folder every user of the PC can already read.
/// </summary>
public static class CollectorLink
{
    public const string PipeName = "CyberneticBandwidth.Collector.v1";

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>One line on the pipe. Payload is the event's data, shaped by <see cref="Type"/>.</summary>
    internal sealed record Message(string Type, JsonElement? Payload = null);

    internal static class Types
    {
        public const string Hello = "hello";
        public const string Usage = "usage";
        public const string Connections = "connections";
        public const string ConnectionEvents = "connectionEvents";
        public const string Throughput = "throughput";
        public const string Status = "status";
        public const string Network = "network";
        public const string Health = "health";
        public const string NetworkEvent = "networkEvent";
        public const string SetHealthChecks = "setHealthChecks";
    }

    internal sealed record HelloPayload(string Version, string DataDirectory, bool HealthChecksEnabled);

    internal sealed record ThroughputPayload(double Down, double Up);

    internal sealed record StatusPayload(string? Message);

    internal sealed record SwitchPayload(bool Enabled);

    /// <summary>Sockets travel as strings: IPEndPoint has no JSON shape of its own.</summary>
    internal sealed record SocketPayload(int Pid, string ProcessName, string? ExecutablePath, byte Protocol,
        string Local, string? Remote, TcpState State)
    {
        public static SocketPayload From(ConnectionSnapshot c) =>
            new(c.Pid, c.ProcessName, c.ExecutablePath, c.Protocol, c.Local.ToString(), c.Remote?.ToString(), c.State);

        public ConnectionSnapshot ToSnapshot() =>
            new(Pid, ProcessName, ExecutablePath, Protocol, IPEndPoint.Parse(Local),
                Remote is null ? null : IPEndPoint.Parse(Remote), State);
    }

    internal static string Encode<T>(string type, T payload) =>
        JsonSerializer.Serialize(new Message(type, JsonSerializer.SerializeToElement(payload, Json)), Json);

    internal static T Decode<T>(Message message) => message.Payload!.Value.Deserialize<T>(Json)!;
}

/// <summary>The service side: forwards everything the broker publishes to every connected window.</summary>
public sealed class CollectorPipeServer(
    LiveActivityBroker broker,
    MonitoringSettings monitoring,
    Func<string> dataDirectory,
    Action<bool> saveHealthChecks,
    ILogger<CollectorPipeServer> log) : BackgroundService
{
    // A window that stops reading must not stall the collector or grow memory without bound.
    private const int QueueLimit = 2_000;

    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Subscribe();

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    CollectorLink.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, PipeAccess());
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not open the collector pipe; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            _ = ServeAsync(pipe, ct);
        }
    }

    private static PipeSecurity PipeAccess()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueLimit)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        // What a window opened now needs straight away, before the next change would tell it.
        string version = typeof(CollectorPipeServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        queue.Writer.TryWrite(CollectorLink.Encode(CollectorLink.Types.Hello,
            new CollectorLink.HelloPayload(version, dataDirectory(), monitoring.HealthChecksEnabled)));
        queue.Writer.TryWrite(CollectorLink.Encode(CollectorLink.Types.Status, new CollectorLink.StatusPayload(broker.StatusMessage)));
        if (broker.LatestNetwork is { } network) queue.Writer.TryWrite(CollectorLink.Encode(CollectorLink.Types.Network, network));
        if (broker.LatestHealth is { } health) queue.Writer.TryWrite(CollectorLink.Encode(CollectorLink.Types.Health, health));

        _clients[id] = queue;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await using (pipe)
            {
                var writing = WriteAsync(pipe, queue.Reader, linked.Token);
                var reading = ReadAsync(pipe, linked.Token);
                await Task.WhenAny(writing, reading);
                linked.Cancel();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The window closed; nothing to report.
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private static async Task WriteAsync(Stream pipe, ChannelReader<string> reader, CancellationToken ct)
    {
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await foreach (var line in reader.ReadAllAsync(ct)) await writer.WriteLineAsync(line.AsMemory(), ct);
    }

    private async Task ReadAsync(Stream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            try
            {
                var message = JsonSerializer.Deserialize<CollectorLink.Message>(line, CollectorLink.Json);
                if (message?.Type == CollectorLink.Types.SetHealthChecks)
                {
                    bool enabled = CollectorLink.Decode<CollectorLink.SwitchPayload>(message).Enabled;
                    monitoring.HealthChecksEnabled = enabled;
                    saveHealthChecks(enabled);
                    log.LogInformation("Quality checks turned {State} from a window.", enabled ? "on" : "off");
                }
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Ignoring a malformed message from a window.");
            }
        }
    }

    private void Subscribe()
    {
        broker.UsageUpdated += s => Broadcast(CollectorLink.Types.Usage, s);
        broker.ConnectionsUpdated += c => Broadcast(CollectorLink.Types.Connections, c.Select(CollectorLink.SocketPayload.From).ToList());
        broker.ConnectionEventsRecorded += e => Broadcast(CollectorLink.Types.ConnectionEvents, e);
        broker.ThroughputUpdated += (down, up) => Broadcast(CollectorLink.Types.Throughput, new CollectorLink.ThroughputPayload(down, up));
        broker.StatusChanged += m => Broadcast(CollectorLink.Types.Status, new CollectorLink.StatusPayload(m));
        broker.NetworkChanged += n => Broadcast(CollectorLink.Types.Network, n);
        broker.HealthUpdated += h => Broadcast(CollectorLink.Types.Health, h);
        broker.NetworkEventRecorded += e => Broadcast(CollectorLink.Types.NetworkEvent, e);
    }

    private void Broadcast<T>(string type, T payload)
    {
        if (_clients.IsEmpty) return;

        string line;
        try
        {
            line = CollectorLink.Encode(type, payload);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not encode a {Type} update for the windows.", type);
            return;
        }

        foreach (var client in _clients.Values) client.Writer.TryWrite(line);
    }
}

/// <summary>
/// The window side while the service is installed: republishes what the service sends through
/// this process's broker, so every page works exactly as it does when collecting itself.
/// </summary>
public sealed class CollectorPipeClient(
    LiveActivityBroker broker,
    MonitoringSettings monitoring,
    ILogger<CollectorPipeClient> log) : BackgroundService
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(3);

    private StreamWriter? _writer;
    private bool _applyingFromService;

    /// <summary>The service's version once connected; null while there is no connection.</summary>
    public string? ServiceVersion { get; private set; }

    public bool IsConnected => ServiceVersion is not null;

    /// <summary>Raised on connecting and disconnecting.</summary>
    public event Action? ConnectionChanged;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        monitoring.Changed += OnMonitoringChanged;
        bool reportedDown = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", CollectorLink.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync((int)RetryEvery.TotalMilliseconds, ct);

                reportedDown = false;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

                while (await reader.ReadLineAsync(ct) is { } line) Handle(line);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Not running yet, restarting, or stopped. Say so once, then keep trying quietly.
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "The link to the Cybernetic Bandwidth Monitor service failed.");
            }

            _writer = null;
            if (ServiceVersion is not null || !reportedDown)
            {
                ServiceVersion = null;
                reportedDown = true;
                broker.PublishStatus("The Cybernetic Bandwidth Monitor service is not answering, so live figures are paused. " +
                                     "Settings › Running in the background shows its state and can start it.");
                ConnectionChanged?.Invoke();
            }

            await Task.Delay(RetryEvery, ct).ContinueWith(_ => { }, CancellationToken.None);
        }

        monitoring.Changed -= OnMonitoringChanged;
    }

    private void Handle(string line)
    {
        CollectorLink.Message? message;
        try
        {
            message = JsonSerializer.Deserialize<CollectorLink.Message>(line, CollectorLink.Json);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Ignoring a malformed update from the service.");
            return;
        }
        if (message?.Payload is null) return;

        switch (message.Type)
        {
            case CollectorLink.Types.Hello:
                var hello = CollectorLink.Decode<CollectorLink.HelloPayload>(message);
                _applyingFromService = true;
                monitoring.HealthChecksEnabled = hello.HealthChecksEnabled;
                _applyingFromService = false;
                ServiceVersion = hello.Version;
                log.LogInformation("Connected to the Cybernetic Bandwidth Monitor service {Version}, data in {Folder}.", hello.Version, hello.DataDirectory);
                ConnectionChanged?.Invoke();
                break;
            case CollectorLink.Types.Usage:
                broker.PublishUsage(CollectorLink.Decode<List<UsageSample>>(message));
                break;
            case CollectorLink.Types.Connections:
                broker.PublishConnections(CollectorLink.Decode<List<CollectorLink.SocketPayload>>(message).Select(s => s.ToSnapshot()).ToList());
                break;
            case CollectorLink.Types.ConnectionEvents:
                broker.PublishConnectionEvents(CollectorLink.Decode<List<ConnectionEventRecord>>(message));
                break;
            case CollectorLink.Types.Throughput:
                var rate = CollectorLink.Decode<CollectorLink.ThroughputPayload>(message);
                broker.PublishThroughput(rate.Down, rate.Up);
                break;
            case CollectorLink.Types.Status:
                broker.PublishStatus(CollectorLink.Decode<CollectorLink.StatusPayload>(message).Message);
                break;
            case CollectorLink.Types.Network:
                broker.PublishNetwork(CollectorLink.Decode<NetworkSnapshot>(message));
                break;
            case CollectorLink.Types.Health:
                broker.PublishHealth(CollectorLink.Decode<HealthSample>(message));
                break;
            case CollectorLink.Types.NetworkEvent:
                broker.PublishNetworkEvent(CollectorLink.Decode<NetworkEventRecord>(message));
                break;
        }
    }

    /// <summary>Settings' quality-check switch belongs to the service while it collects.</summary>
    private void OnMonitoringChanged()
    {
        if (_applyingFromService || _writer is not { } writer) return;
        try
        {
            writer.WriteLine(CollectorLink.Encode(CollectorLink.Types.SetHealthChecks,
                new CollectorLink.SwitchPayload(monitoring.HealthChecksEnabled)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            log.LogWarning(ex, "Could not pass the quality-check switch to the service.");
        }
    }
}
