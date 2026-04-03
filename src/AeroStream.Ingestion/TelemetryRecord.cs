namespace AeroStream.Ingestion;

public record TelemetryRecord(
    Guid DeviceId,
    double Latitude,
    double Longitude,
    double Speed,
    double Pitch,
    double Roll,
    double Yaw,
    DateTime Timestamp
)
{
    public double Altitude { 
        get; 
        init => field = value < 0 ? 0 : value; 
    }
}