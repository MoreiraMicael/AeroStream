using AeroStream.Ingestion;
using System.Threading.Channels;
using Serilog; 
using Scalar.AspNetCore;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. LOGGING SETUP
// ==========================================
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AeroStream")
    .CreateLogger();

builder.Services.AddSerilog(); 

// ==========================================
// 2. SERVICES & DEPENDENCY INJECTION
// ==========================================
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<TelemetryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:5174") 
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); 
    });
});

builder.Services.AddSingleton(Channel.CreateBounded<TelemetryRecord>(1000));
builder.Services.AddHostedService<TelemetryProcessor>();

// UPGRADE: The dictionary now holds a complex C2Payload object, not just a string
builder.Services.AddSingleton<ConcurrentDictionary<string, C2Payload>>();

// PHASE 1: Geofence State Management
builder.Services.AddSingleton<GeofenceState>();

// NEW: Rate Limiter - Max 20 requests per second per IP (DDoS protection)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("telemetryPolicy", opt =>
    {
        opt.PermitLimit = 20; // Allow 20 Hz max per drone
        opt.Window = TimeSpan.FromSeconds(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2; // Allow slight network jitter, then reject
    });
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter(); // MUST be added before mapping endpoints

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");
app.MapHub<TelemetryHub>("/telemetryHub");

// ==========================================
// 4. API ENDPOINTS
// ==========================================

// Existing Endpoint: Individual Drone Commands (e.g., RTL)
app.MapPost("/command/{deviceId}", (string deviceId, CommandRequest req, ConcurrentDictionary<string, C2Payload> commandQueue, ILogger<Program> logger) => 
{
    commandQueue[deviceId] = new C2Payload(req.Command);
    logger.LogInformation("[C2] Command '{Command}' queued for Drone {DeviceId}", req.Command, deviceId);
    return Results.Ok();
});

// NEW Endpoint: Global Swarm Route Update
app.MapPost("/command/swarm/route", (SwarmRouteRequest req, ConcurrentDictionary<string, C2Payload> commandQueue, ILogger<Program> logger) => 
{
    // Loop through the provided drone IDs and assign the new route to each one
    foreach(var id in req.DeviceIds) 
    {
        commandQueue[id] = new C2Payload("UPDATE_ROUTE", req.Route);
    }
    logger.LogInformation("[C2] UPDATE_ROUTE queued for {Count} drones", req.DeviceIds.Length);
    return Results.Ok();
});

// PHASE 1: Geofence Endpoint - Deploy/Update Geofence
app.MapPost("/command/swarm/geofence", (GeofenceRequest req, GeofenceState geofenceState, ILogger<Program> logger) =>
{
    if (req.Coordinates == null || req.Coordinates.Length < 3)
    {
        logger.LogWarning("[GEOFENCE] Invalid geofence: fewer than 3 coordinates");
        return Results.BadRequest("Geofence requires at least 3 coordinates");
    }

    geofenceState.Boundary = req.Coordinates;
    logger.LogInformation("[GEOFENCE] Geofence deployed with {Count} vertices", req.Coordinates.Length);
    return Results.Ok(new { message = "Geofence deployed", vertexCount = req.Coordinates.Length });
});

// The Telemetry Endpoint
app.MapPost("/telemetry", (TelemetryRecord record, Channel<TelemetryRecord> channel, ConcurrentDictionary<string, C2Payload> commandQueue, GeofenceState geofenceState, ILogger<Program> logger) =>
{
    if (!channel.Writer.TryWrite(record))
    {
        logger.LogWarning("Ingestion queue full. Dropping packet for {DeviceId}", record.DeviceId);
        return Results.StatusCode(503);
    }
    
    // PHASE 2: Geofence Check - Ray-Casting Algorithm
    if (geofenceState.Boundary != null && geofenceState.Boundary.Length >= 3)
    {
        var point = new Coordinate(record.Latitude, record.Longitude);
        bool isInsideGeofence = GeofenceHelper.IsPointInPolygon(point, geofenceState.Boundary);
        
        if (!isInsideGeofence)
        {
            logger.LogWarning("[GEOFENCE BREACH] Drone {DeviceId} exited boundary. RTL engaged.", record.DeviceId);
            var rtlPayload = new C2Payload("RTL");
            commandQueue[record.DeviceId.ToString()] = rtlPayload;
            // Return the RTL command immediately
            return Results.Accepted("", rtlPayload);
        }
    }
    
    // UPGRADE: We now extract the C2Payload object and return it directly
    if (commandQueue.TryRemove(record.DeviceId.ToString(), out var payload)) 
    {
        logger.LogInformation("Piggybacking '{Command}' onto ACK for {DeviceId}", payload.Command, record.DeviceId);
        // The framework will automatically serialize the payload (Command + Data) to JSON
        return Results.Accepted("", payload); 
    }

    return Results.Accepted(); 
}).RequireRateLimiting("telemetryPolicy");

app.Run();

// ==========================================
// 5. MODELS
// ==========================================
public record CommandRequest(string Command);

// NEW: Data structures for the dynamic routing
public record Coordinate(double Lat, double Lng);
public record SwarmRouteRequest(string[] DeviceIds, Coordinate[] Route);
public record C2Payload(string Command, object? Data = null);

// PHASE 1: Geofence Models
public record GeofenceRequest(Coordinate[] Coordinates);

public class GeofenceState
{
    private readonly object _lock = new object();
    private Coordinate[]? _boundary;
    
    public Coordinate[]? Boundary
    {
        get
        {
            lock (_lock)
            {
                return _boundary;
            }
        }
        set
        {
            lock (_lock)
            {
                _boundary = value;
            }
        }
    }
}

// PHASE 2: Ray-Casting Algorithm Helper
public static class GeofenceHelper
{
    /// <summary>
    /// Point-in-Polygon using Ray-Casting algorithm.
    /// Casts a horizontal ray from the point to infinity and counts boundary crossings.
    /// </summary>
    public static bool IsPointInPolygon(Coordinate point, Coordinate[] polygon)
    {
        if (polygon.Length < 3) return false;

        int crossings = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Coordinate a = polygon[i];
            Coordinate b = polygon[(i + 1) % polygon.Length]; // Wrap to first vertex

            // Check if ray crosses this edge
            if (IsRayIntersectingEdge(point, a, b))
            {
                crossings++;
            }
        }

        // Odd number of crossings = point is inside
        return crossings % 2 == 1;
    }

    private static bool IsRayIntersectingEdge(Coordinate point, Coordinate a, Coordinate b)
    {
        // Edge must straddle the horizontal ray
        if ((a.Lat <= point.Lat && b.Lat > point.Lat) || (b.Lat <= point.Lat && a.Lat > point.Lat))
        {
            // Calculate x-coordinate of intersection
            double xIntersect = a.Lng + (point.Lat - a.Lat) / (b.Lat - a.Lat) * (b.Lng - a.Lng);
            
            // Ray extends to the right (positive infinity)
            return point.Lng < xIntersect;
        }

        return false;
    }
}