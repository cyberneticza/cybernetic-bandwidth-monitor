using Microsoft.EntityFrameworkCore;
using CyberneticBandwidth.Data.Entities;

namespace CyberneticBandwidth.Data;

public sealed class CyberneticBandwidthDbContext(DbContextOptions<CyberneticBandwidthDbContext> options) : DbContext(options)
{
    public DbSet<AppEntity> Apps => Set<AppEntity>();
    public DbSet<UsageBucketEntity> UsageBuckets => Set<UsageBucketEntity>();
    public DbSet<ConnectionEventEntity> ConnectionEvents => Set<ConnectionEventEntity>();
    public DbSet<WatermarkEntity> Watermarks => Set<WatermarkEntity>();
    public DbSet<NetworkEventEntity> NetworkEvents => Set<NetworkEventEntity>();
    public DbSet<HealthSampleEntity> HealthSamples => Set<HealthSampleEntity>();
    public DbSet<SpeedTestEntity> SpeedTests => Set<SpeedTestEntity>();
    public DbSet<RegionalSpeedTestEntity> RegionalSpeedTests => Set<RegionalSpeedTestEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppEntity>(e =>
        {
            e.ToTable("Apps");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AttributionId).IsUnique();
        });

        b.Entity<UsageBucketEntity>(e =>
        {
            e.ToTable("UsageBuckets");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AppId, x.Granularity, x.BucketStartUnix, x.InterfaceKind }).IsUnique();
            e.HasIndex(x => new { x.Granularity, x.BucketStartUnix });
        });

        b.Entity<ConnectionEventEntity>(e =>
        {
            e.ToTable("ConnectionEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ObservedUnix);
        });

        b.Entity<WatermarkEntity>(e =>
        {
            e.ToTable("Watermarks");
            e.HasKey(x => x.Key);
        });

        b.Entity<NetworkEventEntity>(e =>
        {
            e.ToTable("NetworkEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ObservedUnix);
        });

        b.Entity<HealthSampleEntity>(e =>
        {
            e.ToTable("HealthSamples");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ObservedUnix);
        });

        b.Entity<SpeedTestEntity>(e =>
        {
            e.ToTable("SpeedTests");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ObservedUnix);
        });

        b.Entity<RegionalSpeedTestEntity>(e =>
        {
            e.ToTable("RegionalSpeedTests");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RegionCode, x.ObservedUnix });
        });
    }
}
