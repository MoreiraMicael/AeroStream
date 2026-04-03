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
}
}