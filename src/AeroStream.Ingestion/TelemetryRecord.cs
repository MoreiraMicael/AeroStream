namespace AeroStream.Ingestion;

public record TelemetryRecord(
    Guid DeviceId,
    double Latitude,
    double Longitude,
    double Speed,
    double Pitch,
    double Roll,
    double Yaw,
    DateTime Timestamp,
    double BatteryVoltage = 0.0   // Volts — populated by real hardware, 0 from simulator
)
{
    public double Altitude { 
        get; 
        init => field = value < 0 ? 0 : value; 
    }
}