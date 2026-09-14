using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CyberneticBandwidth.Data;

public interface ISchemaInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}

/// <summary>
/// Creates the schema with explicit DDL rather than EF migrations.
///
/// Why: the hot write path is a hand-written upsert that depends on the exact unique index
/// (AppId, Granularity, BucketStartUnix, InterfaceKind) existing as the ON CONFLICT target, and
/// the rollup is raw SQL. Keeping the DDL in one readable place next to those statements is
/// worth more here than generated migrations. If you later want migrations, run
/// `dotnet ef migrations add Initial` and swap InitializeAsync for Database.MigrateAsync() —
/// the model in CyberneticBandwidthDbContext already matches this DDL exactly.
/// </summary>
public sealed class SchemaInitializer(
    CyberneticBandwidthDbContext db,
    IOptions<StorageOptions> storage,
    ILogger<SchemaInitializer> log) : ISchemaInitializer
{
    private const int SchemaVersion = 4;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(storage.Value.DataDirectory);
        Directory.CreateDirectory(storage.Value.LogDirectory);

        await db.Database.OpenConnectionAsync(ct);

        // WAL keeps the UI's reads from blocking behind the sampler's writes.
        // synchronous=NORMAL is the right trade for telemetry: a crash can cost the last
        // transaction, and the next NDU pass re-reads it anyway.
        await db.Database.ExecuteSqlRawAsync("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA auto_vacuum = INCREMENTAL;
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Apps (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                AttributionId   TEXT    NOT NULL,
                DisplayName     TEXT    NOT NULL,
                ExecutablePath  TEXT    NULL,
                FirstSeenUnix   INTEGER NOT NULL,
                LastSeenUnix    INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_Apps_AttributionId ON Apps(AttributionId);

            CREATE TABLE IF NOT EXISTS UsageBuckets (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                AppId           INTEGER NOT NULL REFERENCES Apps(Id) ON DELETE CASCADE,
                Granularity     INTEGER NOT NULL,
                BucketStartUnix INTEGER NOT NULL,
                InterfaceKind   INTEGER NOT NULL,
                BytesSent       INTEGER NOT NULL,
                BytesReceived   INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_UsageBuckets
                ON UsageBuckets(AppId, Granularity, BucketStartUnix, InterfaceKind);

            CREATE INDEX IF NOT EXISTS IX_UsageBuckets_Scan
                ON UsageBuckets(Granularity, BucketStartUnix);

            CREATE TABLE IF NOT EXISTS ConnectionEvents (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ObservedUnix  INTEGER NOT NULL,
                Kind          INTEGER NOT NULL,
                Pid           INTEGER NOT NULL,
                ProcessName   TEXT    NOT NULL,
                Protocol      INTEGER NOT NULL,
                LocalAddress  TEXT    NOT NULL,
                LocalPort     INTEGER NOT NULL,
                RemoteAddress TEXT    NULL,
                RemotePort    INTEGER NULL,
                TcpState      INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ConnectionEvents_Time
                ON ConnectionEvents(ObservedUnix DESC);

            CREATE TABLE IF NOT EXISTS Watermarks (
                Key       TEXT    PRIMARY KEY,
                ValueUnix INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Version INTEGER NOT NULL
            );

            -- Version 2: the connection itself. Plain appends read back by time range, so an
            -- index on time is all they need.
            CREATE TABLE IF NOT EXISTS NetworkEvents (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ObservedUnix INTEGER NOT NULL,
                Kind         INTEGER NOT NULL,
                Title        TEXT    NOT NULL,
                Detail       TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_NetworkEvents_Time ON NetworkEvents(ObservedUnix);

            CREATE TABLE IF NOT EXISTS HealthSamples (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ObservedUnix        INTEGER NOT NULL,
                Status              INTEGER NOT NULL,
                GatewayLatencyMs    REAL    NULL,
                InternetLatencyMs   REAL    NULL,
                InternetJitterMs    REAL    NULL,
                InternetLossPercent REAL    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_HealthSamples_Time ON HealthSamples(ObservedUnix);

            CREATE TABLE IF NOT EXISTS SpeedTests (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ObservedUnix  INTEGER NOT NULL,
                DownloadMbps  REAL    NOT NULL,
                UploadMbps    REAL    NOT NULL,
                LatencyMs     REAL    NOT NULL,
                JitterMs      REAL    NOT NULL,
                Server        TEXT    NOT NULL,
                PublicAddress TEXT    NULL,
                NetworkName   TEXT    NULL,
                Medium        INTEGER NOT NULL,
                BytesUsed     INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_SpeedTests_Time ON SpeedTests(ObservedUnix);

            -- Version 3: regional speed tests. Read back as the latest row per region.
            CREATE TABLE IF NOT EXISTS RegionalSpeedTests (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ObservedUnix INTEGER NOT NULL,
                RegionCode   TEXT    NOT NULL,
                LatencyMs    REAL    NULL,
                JitterMs     REAL    NULL,
                DownloadMbps REAL    NULL,
                BytesUsed    INTEGER NOT NULL,
                NetworkName  TEXT    NULL,
                Error        TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_RegionalSpeedTests_Region ON RegionalSpeedTests(RegionCode, ObservedUnix);
            """, ct);

        // Version 4: regional upload. Added to an existing table rather than written into the CREATE
        // above, which a version 3 database has already run and skips.
        int hasUpload = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('RegionalSpeedTests') WHERE name = 'UploadMbps'")
            .SingleAsync(ct);
        if (hasUpload == 0)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE RegionalSpeedTests ADD COLUMN UploadMbps REAL NULL", ct);

        // MAX() always yields exactly one row, so Single; First without an OrderBy logs an EF warning.
        int current = await db.Database
            .SqlQueryRaw<int?>("SELECT MAX(Version) AS Value FROM SchemaInfo")
            .SingleAsync(ct) ?? 0;

        if (current < SchemaVersion)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO SchemaInfo (Version) VALUES ({SchemaVersion})", ct);
            log.LogInformation("Schema initialised at version {Version} ({Path}).",
                SchemaVersion, storage.Value.DatabasePath);
        }
    }
}
