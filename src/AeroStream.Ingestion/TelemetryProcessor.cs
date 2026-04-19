using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AeroStream.Ingestion;

public class TelemetryProcessor(
    Channel<TelemetryRecord> channel, 
    IHubContext<TelemetryHub> hubContext,
    IDbContextFactory<TelemetryDbContext> dbFactory,
    ILogger<TelemetryProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // --- RESILIENT STARTUP LOOP ---
        // This ensures the API doesn't crash if the DB is still booting up
        bool isDbReady = false;
        while (!isDbReady && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var context = await dbFactory.CreateDbContextAsync(stoppingToken);
                await context.Database.EnsureCreatedAsync(stoppingToken);
                isDbReady = true;
                logger.LogInformation("Database connection established and schema ensured.");
            }
            catch (Exception)
            {
                logger.LogWarning("Database not ready yet. Retrying in 2 seconds...");
                await Task.Delay(2000, stoppingToken);
            }
        }

        // --- MAIN PROCESSING LOOP (BATCHED PERSISTENCE) ---
        const int BATCH_SIZE = 50;
        const int BATCH_TIMEOUT_MS = 500;
        
        var batch = new List<TelemetryRecord>(BATCH_SIZE);
        var lastFlushTime = DateTime.UtcNow;
        
        await foreach (var record in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try 
            {  
                // 1. BROADCAST FIRST (Real-time speed for the pilot)
                // SignalR will automatically camelCase these properties (e.g., Latitude -> latitude)
                await hubContext.Clients.All.SendAsync("ReceiveTelemetry", record, stoppingToken);

                // 2. ADD TO BATCH (accumulate for bulk insert)
                batch.Add(record);
                var timeSinceLastFlush = (DateTime.UtcNow - lastFlushTime).TotalMilliseconds;

                // 3. FLUSH BATCH IF: batch full OR timeout exceeded
                if (batch.Count >= BATCH_SIZE || timeSinceLastFlush >= BATCH_TIMEOUT_MS)
                {
                    await PersistBatch(batch, stoppingToken);
                    batch.Clear();
                    lastFlushTime = DateTime.UtcNow;
                    logger.LogInformation("Flushed batch to database");
                }
            }
            catch (Exception ex)
            {
                // We log the error but DON'T stop the loop. 
                // One bad packet shouldn't crash the entire Ground Control Station.
                logger.LogError("Telemetry processing failed: {Msg}", ex.Message);
            }
        }

        // Flush any remaining records on shutdown
        if (batch.Count > 0)
        {
            try
            {
                await PersistBatch(batch, stoppingToken);
                logger.LogInformation("Flushed final batch ({Count} records) on shutdown", batch.Count);
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to flush final batch: {Msg}", ex.Message);
            }
        }
    }

    // Helper method: persist a batch of records using AddRange (single SaveChangesAsync)
    private async Task PersistBatch(List<TelemetryRecord> records, CancellationToken stoppingToken)
    {
        if (records.Count == 0)
            return;

        try
        {
            using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            db.Telemetry.AddRange(records);
            await db.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Persisted batch of {Count} records to database", records.Count);
        }
        catch (Exception ex)
        {
            logger.LogError("Batch persistence failed: {Msg}. Records in batch: {Count}", ex.Message, records.Count);
            throw;
        }
    }
}