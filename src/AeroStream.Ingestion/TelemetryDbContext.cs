using Microsoft.EntityFrameworkCore;

namespace AeroStream.Ingestion;

public class TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryRecord> Telemetry => Set<TelemetryRecord>();

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // Create an "Id" property that the DB handles automatically
    modelBuilder.Entity<TelemetryRecord>()
        .Property<int>("Id")
        .ValueGeneratedOnAdd();

    modelBuilder.Entity<TelemetryRecord>()
        .HasKey("Id"); // Use the auto-id as the key instead of timestamp

    // NEW: Composite index for lightning-fast historical lookups
    modelBuilder.Entity<TelemetryRecord>()
        .HasIndex(t => new { t.DeviceId, t.Timestamp })
        .IsDescending(false, true); // Descending on Timestamp for recent-data queries
}
}