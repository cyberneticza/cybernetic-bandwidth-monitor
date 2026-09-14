using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;

namespace CyberneticBandwidth.Core.Services;

/// <summary>Stores a network event and tells the UI, for both services that notice them.</summary>
public sealed class NetworkEventRecorder(
    IServiceScopeFactory scopes,
    LiveActivityBroker broker,
    ILogger<NetworkEventRecorder> log)
{
    public async Task RecordAsync(NetworkEventKind kind, string title, string? detail, CancellationToken ct)
    {
        var record = new NetworkEventRecord(DateTimeOffset.UtcNow, kind, title, detail);
        log.LogInformation("Network event: {Title}. {Detail}", title, detail);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INetworkHistoryRepository>().InsertNetworkEventAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not store the network event '{Title}'.", title);
        }

        broker.PublishNetworkEvent(record);
    }

    /// <summary>"3 min 12 s", "1 h 4 min": short enough for an event line.</summary>
    public static string Duration(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.Seconds} s",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min {span.Seconds} s",
        { TotalHours: < 24 } => $"{(int)span.TotalHours} h {span.Minutes} min",
        _ => $"{(int)span.TotalDays} d {span.Hours} h"
    };
}
