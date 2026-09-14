using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NetTrace.Core.Abstractions;
using NetTrace.Core.Models;
using NetTrace.Core.Services;
using NetTrace.Data.Entities;

namespace NetTrace.Data;

public sealed class UsageRepository(
    NetTraceDbContext db,
    IOptions<RetentionOptions> retention,
    IOptions<StorageOptions> storage) : IUsageRepository
{
    // ---------------------------------------------------------------- writes

    public async Task UpsertBucketsAsync(IReadOnlyList<UsageSample> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return;

        var appIds = await EnsureAppsAsync(samples, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO UsageBuckets
                (AppId, Granularity, BucketStartUnix, InterfaceKind, BytesSent, BytesReceived)
            VALUES ($app, $granularity, $bucket, $interface, $sent, $received)
            ON CONFLICT(AppId, Granularity, BucketStartUnix, InterfaceKind)
            DO UPDATE SET BytesSent = excluded.BytesSent,
                          BytesReceived = excluded.BytesReceived;
            """;

        // Replace rather than add. Samples are absolute totals for their bucket, so re-reading an
        // overlapping window (which the sampler does on every pass, deliberately) is a no-op
        // instead of a double count.
        var pApp = command.Parameters.Add("$app", SqliteType.Integer);
        var pGranularity = command.Parameters.Add("$granularity", SqliteType.Integer);
        var pBucket = command.Parameters.Add("$bucket", SqliteType.Integer);
        var pInterface = command.Parameters.Add("$interface", SqliteType.Integer);
        var pSent = command.Parameters.Add("$sent", SqliteType.Integer);
        var pReceived = command.Parameters.Add("$received", SqliteType.Integer);

        await command.PrepareAsync(ct);

        foreach (var sample in samples)
        {
            if (!appIds.TryGetValue(sample.AttributionId, out long appId)) continue;

            pApp.Value = appId;
            pGranularity.Value = (int)sample.Granularity;
            pBucket.Value = sample.BucketStartUtc.ToUnixTimeSeconds();
            pInterface.Value = (int)sample.Interface;
            pSent.Value = sample.BytesSent;
            pReceived.Value = sample.BytesReceived;

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<int> RefreshAppNamesAsync(Func<string, string> resolveName, CancellationToken ct)
    {
        var apps = await db.Apps.ToListAsync(ct);
        int renamed = 0;

        foreach (var app in apps)
        {
            // "name:" rows are traffic NDU gave no id for, already named as well as they can be.
            if (app.AttributionId.StartsWith("name:", StringComparison.Ordinal)) continue;

            string name = resolveName(app.AttributionId);
            if (name == app.DisplayName) continue;
            app.DisplayName = name;
            renamed++;
        }

        if (renamed > 0) await db.SaveChangesAsync(ct);
        return renamed;
    }

    private async Task<Dictionary<string, long>> EnsureAppsAsync(
        IReadOnlyList<UsageSample> samples, CancellationToken ct)
    {
        var wanted = samples
            .GroupBy(s => s.AttributionId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        // A List, not wanted.Keys — EF cannot translate a KeyCollection into an IN clause.
        var attributionIds = wanted.Keys.ToList();

        var existing = await db.Apps
            .Where(a => attributionIds.Contains(a.AttributionId))
            .ToDictionaryAsync(a => a.AttributionId, ct);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var added = new List<AppEntity>();

        foreach (var (attributionId, displayName) in wanted)
        {
            if (existing.TryGetValue(attributionId, out var app))
            {
                app.LastSeenUnix = now;
                // NDU occasionally starts returning a better friendly name once an app has run.
                if (!string.IsNullOrWhiteSpace(displayName)) app.DisplayName = displayName;
                continue;
            }

            var entity = new AppEntity
            {
                AttributionId = attributionId,
                DisplayName = displayName,
                FirstSeenUnix = now,
                LastSeenUnix = now
            };
            added.Add(entity);
            db.Apps.Add(entity);
        }

        if (added.Count > 0 || db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        foreach (var entity in added) existing[entity.AttributionId] = entity;

        return existing.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    }

    public async Task InsertConnectionEventsAsync(
        IReadOnlyList<ConnectionEventRecord> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        db.ConnectionEvents.AddRange(events.Select(e => new ConnectionEventEntity
        {
            ObservedUnix = e.ObservedUtc.ToUnixTimeSeconds(),
            Kind = (int)e.Kind,
            Pid = e.Pid,
            ProcessName = e.ProcessName,
            Protocol = e.Protocol,
            LocalAddress = e.LocalAddress,
            LocalPort = e.LocalPort,
            RemoteAddress = e.RemoteAddress,
            RemotePort = e.RemotePort,
            TcpState = (int)e.State
        }));

        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- reads

    public async Task<IReadOnlyList<UsagePoint>> GetTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bin, CancellationToken ct)
    {
        var (fromUnix, toUnix, binSeconds, granularities) = TimelineBounds(from, to, bin);

        var rows = await db.UsageBuckets
            .Where(b => granularities.Contains(b.Granularity)
                        && b.BucketStartUnix >= fromUnix && b.BucketStartUnix < toUnix)
            .GroupBy(b => new { b.Granularity, Bin = b.BucketStartUnix / binSeconds * binSeconds })
            .Select(g => new
            {
                g.Key.Granularity,
                g.Key.Bin,
                Sent = g.Sum(x => x.BytesSent),
                Received = g.Sum(x => x.BytesReceived)
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Bin)
            .Select(g => g.MaxBy(r => r.Sent + r.Received)!)
            .OrderBy(r => r.Bin)
            .Select(r => new UsagePoint(DateTimeOffset.FromUnixTimeSeconds(r.Bin), r.Sent, r.Received))
            .ToList();
    }

    public async Task<IReadOnlyList<UsageSeries>> GetAppTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bin, CancellationToken ct)
    {
        var (fromUnix, toUnix, binSeconds, granularities) = TimelineBounds(from, to, bin);

        // Interface kinds collapse here: the charts show traffic per app, not per medium.
        var rows = await db.UsageBuckets
            .Where(b => granularities.Contains(b.Granularity)
                        && b.BucketStartUnix >= fromUnix && b.BucketStartUnix < toUnix)
            .GroupBy(b => new { b.AppId, b.App!.DisplayName, b.Granularity, Bin = b.BucketStartUnix / binSeconds * binSeconds })
            .Select(g => new
            {
                g.Key.AppId,
                g.Key.DisplayName,
                g.Key.Granularity,
                g.Key.Bin,
                Sent = g.Sum(x => x.BytesSent),
                Received = g.Sum(x => x.BytesReceived)
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.AppId, r.Bin })
            .Select(g => g.MaxBy(r => r.Sent + r.Received)!)
            .GroupBy(r => new { r.AppId, r.DisplayName })
            .Select(g => new UsageSeries(
                g.Key.AppId,
                g.Key.DisplayName,
                g.OrderBy(r => r.Bin)
                 .Select(r => new UsagePoint(DateTimeOffset.FromUnixTimeSeconds(r.Bin), r.Sent, r.Received))
                 .ToList()))
            .ToList();
    }

    /// <summary>
    /// A bin is filled from every granularity no coarser than itself, and each bin keeps whichever
    /// granularity recorded the most. Rollups run hourly, so the newest hour exists only as
    /// minutes and the newest day only as minutes and hours; history from before the first run is
    /// the reverse, backfilled as hours with no minutes under it. Summing across granularities
    /// would count promoted traffic twice; taking the largest picks the most complete record.
    /// </summary>
    private static (long From, long To, long BinSeconds, int[] Granularities) TimelineBounds(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bin)
    {
        long binSeconds = Math.Max(60, (long)bin.TotalSeconds);
        int[] granularities = Enum.GetValues<Granularity>()
            .Where(g => g.BucketSeconds() <= binSeconds)
            .Select(g => (int)g)
            .ToArray();

        return (from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds(), binSeconds, granularities);
    }

    public async Task<IReadOnlyList<ConnectionEventRecord>> GetRecentConnectionEventsAsync(
        int take, CancellationToken ct)
    {
        var rows = await db.ConnectionEvents
            .OrderByDescending(e => e.ObservedUnix)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(e => new ConnectionEventRecord(
            DateTimeOffset.FromUnixTimeSeconds(e.ObservedUnix),
            (ConnectionEventKind)e.Kind,
            e.Pid,
            e.ProcessName,
            (byte)e.Protocol,
            e.LocalAddress,
            e.LocalPort,
            e.RemoteAddress,
            e.RemotePort,
            (System.Net.NetworkInformation.TcpState)e.TcpState)).ToList();
    }

    public async Task<DateTimeOffset?> GetWatermarkAsync(string key, CancellationToken ct)
    {
        var row = await db.Watermarks.AsNoTracking().FirstOrDefaultAsync(w => w.Key == key, ct);
        return row is null ? null : DateTimeOffset.FromUnixTimeSeconds(row.ValueUnix);
    }

    public async Task SetWatermarkAsync(string key, DateTimeOffset value, CancellationToken ct)
    {
        long unix = value.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Watermarks (Key, ValueUnix) VALUES ({key}, {unix})
             ON CONFLICT(Key) DO UPDATE SET ValueUnix = excluded.ValueUnix;
             """, ct);
    }

    public Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct)
    {
        var file = new FileInfo(storage.Value.DatabasePath);
        return Task.FromResult(file.Exists ? file.Length : 0L);
    }

    // ---------------------------------------------------------------- maintenance

    public async Task RunRollupAsync(CancellationToken ct)
    {
        await PromoteAsync(Granularity.Minute, Granularity.Hour, ct);
        await PromoteAsync(Granularity.Hour, Granularity.Day, ct);
        await PruneAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", ct);
    }

    private Task PromoteAsync(Granularity from, Granularity to, CancellationToken ct)
    {
        long bucketSeconds = to.BucketSeconds();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO UsageBuckets
                (AppId, Granularity, BucketStartUnix, InterfaceKind, BytesSent, BytesReceived)
            SELECT AppId,
                   {(int)to},
                   (BucketStartUnix / {bucketSeconds}) * {bucketSeconds},
                   InterfaceKind,
                   SUM(BytesSent),
                   SUM(BytesReceived)
            FROM UsageBuckets
            WHERE Granularity = {(int)from}
            GROUP BY AppId, (BucketStartUnix / {bucketSeconds}) * {bucketSeconds}, InterfaceKind
            ON CONFLICT(AppId, Granularity, BucketStartUnix, InterfaceKind)
            DO UPDATE SET BytesSent = excluded.BytesSent,
                          BytesReceived = excluded.BytesReceived;
            """, ct);
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var options = retention.Value;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Cut on the parent bucket's boundary, never mid-bucket. Deleting half an hour's worth of
        // minute rows would make the next promotion overwrite that hour with a partial sum.
        long minuteCut = Floor(now - options.MinuteBucketDays * 86_400L, 3_600);
        long hourCut = Floor(now - options.HourBucketDays * 86_400L, 86_400);
        long dayCut = now - options.DayBucketDays * 86_400L;
        long eventCut = now - options.ConnectionEventDays * 86_400L;
        long healthCut = now - options.HealthSampleDays * 86_400L;
        long networkEventCut = now - options.NetworkEventDays * 86_400L;

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM UsageBuckets WHERE Granularity = 0 AND BucketStartUnix < {minuteCut};", ct);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM UsageBuckets WHERE Granularity = 1 AND BucketStartUnix < {hourCut};", ct);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM UsageBuckets WHERE Granularity = 2 AND BucketStartUnix < {dayCut};", ct);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM ConnectionEvents WHERE ObservedUnix < {eventCut};", ct);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM HealthSamples WHERE ObservedUnix < {healthCut};", ct);
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM NetworkEvents WHERE ObservedUnix < {networkEventCut};", ct);
        // Speed tests are a handful of rows a week at most, and old ones are the baseline.
    }

    private static long Floor(long unixSeconds, long bucketSeconds) =>
        unixSeconds - (unixSeconds % bucketSeconds);
}
