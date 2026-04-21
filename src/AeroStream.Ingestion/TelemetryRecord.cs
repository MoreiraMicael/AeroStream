namespace AeroStream.Ingestion;

using System.ComponentModel.DataAnnotations.Schema;

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
    [NotMapped]
    public string? DroneType { get; init; }

    public double Altitude { 
        get; 
        init => field = value < 0 ? 0 : value; 
    }
}