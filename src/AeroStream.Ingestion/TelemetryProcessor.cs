using System.Threading.Channels;

namespace AeroStream.Ingestion;

public class TelemetryProcessor(Channel<TelemetryRecord> channel, ILogger<TelemetryProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry Processor starting...");
        await foreach (var record in channel.Reader.ReadAllAsync(stoppingToken))
        {
            logger.LogInformation("[REC] Drone {Id} at {Alt}m", record.DeviceId, record.Altitude);
        }
    }
}