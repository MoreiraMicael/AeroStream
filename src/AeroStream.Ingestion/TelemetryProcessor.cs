using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR; // Add this

namespace AeroStream.Ingestion;

public class TelemetryProcessor(
    Channel<TelemetryRecord> channel, 
    IHubContext<TelemetryHub> hubContext, // Inject the Hub
    ILogger<TelemetryProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry Processor starting with SignalR support...");

        await foreach (var record in channel.Reader.ReadAllAsync(stoppingToken))
        {
            // 1. Log it (Structured Logging)
            logger.LogInformation("[LIVE] Drone {Id} at {Alt}m", record.DeviceId, record.Altitude);

            // 2. Push it to the Real-Time Hub
            // This sends the telemetry to all connected clients (dashboards) instantly.
            await hubContext.Clients.All.SendAsync("ReceiveTelemetry", record, cancellationToken: stoppingToken);
        }
    }
}