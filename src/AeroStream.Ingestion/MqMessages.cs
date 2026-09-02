namespace AeroStream.Ingestion;

public record DispatchMessage(string[] DroneIds, string Command, object? Data);
public record AlertMessage(string DroneId, string AlertType, double Value, DateTime Timestamp);
