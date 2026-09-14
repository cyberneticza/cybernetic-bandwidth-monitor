using Microsoft.EntityFrameworkCore;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Data.Entities;

namespace CyberneticBandwidth.Data;

public sealed class NetworkHistoryRepository(CyberneticBandwidthDbContext db) : INetworkHistoryRepository
{
    public async Task InsertNetworkEventAsync(NetworkEventRecord record, CancellationToken ct)
    {
        db.NetworkEvents.Add(new NetworkEventEntity
        {
            ObservedUnix = record.ObservedUtc.ToUnixTimeSeconds(),
            Kind = (int)record.Kind,
            Title = record.Title,
            Detail = record.Detail
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NetworkEventRecord>> GetNetworkEventsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        long f = from.ToUnixTimeSeconds(), t = to.ToUnixTimeSeconds();

        return await db.NetworkEvents.AsNoTracking()
            .Where(e => e.ObservedUnix >= f && e.ObservedUnix < t)
            .OrderBy(e => e.ObservedUnix).ThenBy(e => e.Id)
            .Select(e => new NetworkEventRecord(
                DateTimeOffset.FromUnixTimeSeconds(e.ObservedUnix), (NetworkEventKind)e.Kind, e.Title, e.Detail))
            .ToListAsync(ct);
    }

    public async Task InsertHealthSampleAsync(HealthSample sample, CancellationToken ct)
    {
        db.HealthSamples.Add(new HealthSampleEntity
        {
            ObservedUnix = sample.ObservedUtc.ToUnixTimeSeconds(),
            Status = (int)sample.Status,
            GatewayLatencyMs = sample.GatewayLatencyMs,
            InternetLatencyMs = sample.InternetLatencyMs,
            InternetJitterMs = sample.InternetJitterMs,
            InternetLossPercent = sample.InternetLossPercent
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HealthSample>> GetHealthSamplesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        long f = from.ToUnixTimeSeconds(), t = to.ToUnixTimeSeconds();

        return await db.HealthSamples.AsNoTracking()
            .Where(s => s.ObservedUnix >= f && s.ObservedUnix < t)
            .OrderBy(s => s.ObservedUnix)
            .Select(s => new HealthSample(
                DateTimeOffset.FromUnixTimeSeconds(s.ObservedUnix), (HealthStatus)s.Status,
                s.GatewayLatencyMs, s.InternetLatencyMs, s.InternetJitterMs, s.InternetLossPercent))
            .ToListAsync(ct);
    }

    public async Task InsertSpeedTestAsync(SpeedTestResult result, CancellationToken ct)
    {
        db.SpeedTests.Add(new SpeedTestEntity
        {
            ObservedUnix = result.ObservedUtc.ToUnixTimeSeconds(),
            DownloadMbps = result.DownloadMbps,
            UploadMbps = result.UploadMbps,
            LatencyMs = result.LatencyMs,
            JitterMs = result.JitterMs,
            Server = result.Server,
            PublicAddress = result.PublicAddress,
            NetworkName = result.NetworkName,
            Medium = (int)result.Medium,
            BytesUsed = result.BytesUsed
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SpeedTestResult>> GetSpeedTestsAsync(int take, CancellationToken ct)
    {
        var rows = await db.SpeedTests.AsNoTracking()
            .OrderByDescending(s => s.ObservedUnix).ThenByDescending(s => s.Id)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(s => new SpeedTestResult(
            DateTimeOffset.FromUnixTimeSeconds(s.ObservedUnix), s.DownloadMbps, s.UploadMbps, s.LatencyMs,
            s.JitterMs, s.Server, s.PublicAddress, s.NetworkName, (ConnectionMedium)s.Medium, s.BytesUsed)).ToList();
    }

    public async Task InsertRegionalSpeedTestAsync(RegionalSpeedResult result, CancellationToken ct)
    {
        db.RegionalSpeedTests.Add(new RegionalSpeedTestEntity
        {
            ObservedUnix = result.ObservedUtc.ToUnixTimeSeconds(),
            RegionCode = result.RegionCode,
            LatencyMs = result.LatencyMs,
            JitterMs = result.JitterMs,
            DownloadMbps = result.DownloadMbps,
            UploadMbps = result.UploadMbps,
            BytesUsed = result.BytesUsed,
            NetworkName = result.NetworkName,
            Error = result.Error
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RegionalSpeedResult>> GetRegionalSpeedTestsAsync(int take, CancellationToken ct)
    {
        var rows = await db.RegionalSpeedTests.AsNoTracking()
            .OrderByDescending(r => r.ObservedUnix).ThenByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(ct);

        return rows
            .Select(r => new RegionalSpeedResult(
                DateTimeOffset.FromUnixTimeSeconds(r.ObservedUnix), r.RegionCode, r.LatencyMs, r.JitterMs,
                r.DownloadMbps, r.UploadMbps, r.BytesUsed, r.NetworkName, r.Error))
            .ToList();
    }
}