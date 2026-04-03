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

        // --- MAIN PROCESSING LOOP ---
        await foreach (var record in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try 
            {  
                // 1. BROADCAST FIRST (Real-time speed for the pilot)
                // SignalR will automatically camelCase these properties (e.g., Latitude -> latitude)
                await hubContext.Clients.All.SendAsync("ReceiveTelemetry", record, stoppingToken);

                // 2. SAVE SECOND (The "Black Box" persistence)
                using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                db.Telemetry.Add(record);
                await db.SaveChangesAsync(stoppingToken);

                logger.LogInformation("Processed {DeviceId}: Alt {Alt}m, Spd {Spd}km/h", 
                    record.DeviceId, record.Altitude, record.Speed);
            }
            catch (Exception ex)
            {
                // We log the error but DON'T stop the loop. 
                // One bad packet shouldn't crash the entire Ground Control Station.
                logger.LogError("Telemetry processing failed: {Msg}", ex.Message);
            }
        }
    }
}