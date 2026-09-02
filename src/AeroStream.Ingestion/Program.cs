using AeroStream.Ingestion;
using System.Threading.Channels;
using Serilog;
using Scalar.AspNetCore;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Prometheus;

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

builder.Services.AddHealthChecks()
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

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

builder.Services.AddSingleton<ConcurrentDictionary<string, C2Payload>>();

builder.Services.AddSingleton<GeofenceState>();

// RabbitMQ: publisher singleton + consumer background service
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddSingleton<IRabbitMqPublisher>(sp => sp.GetRequiredService<RabbitMqPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqPublisher>());
builder.Services.AddHostedService<CommandDispatchConsumer>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("telemetryPolicy", httpContext =>
    {
        var deviceId = httpContext.Request.Headers["X-Drone-Id"].ToString();
        var partitionKey = !string.IsNullOrWhiteSpace(deviceId)
            ? $"drone:{deviceId}"
            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 2
        });
    });
});

var app = builder.Build();

app.UseCors();
app.UseHttpMetrics();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");
app.MapMetrics();
app.MapHub<TelemetryHub>("/telemetryHub");

// ==========================================
// 4. API ENDPOINTS
// ==========================================

app.MapPost("/command/{deviceId}", async (string deviceId, CommandRequest req, IRabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    try
    {
        var routingKey = req.Command == "RTL" ? "command.rtl" : "command.drone";
        await publisher.PublishAsync(routingKey, new DispatchMessage([deviceId], req.Command, null));
        logger.LogInformation("[C2] Command '{Command}' published for Drone {DeviceId}", req.Command, deviceId);
        if (req.Command == "RTL")
            IngestMetrics.RtlTriggered.WithLabels("operator_manual").Inc();
        return Results.Ok();
    }
    catch (Exception ex)
    {
        logger.LogError("[C2] Failed to publish command for {DeviceId}: {Msg}", deviceId, ex.Message);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/command/swarm/route", async (SwarmRouteRequest req, IRabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    try
    {
        await publisher.PublishAsync("command.swarm.route", new DispatchMessage(req.DeviceIds, "UPDATE_ROUTE", req.Route));
        logger.LogInformation("[C2] UPDATE_ROUTE published for {Count} drones", req.DeviceIds.Length);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        logger.LogError("[C2] Failed to publish swarm route: {Msg}", ex.Message);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/command/swarm/geofence", async (GeofenceRequest req, GeofenceState geofenceState, IRabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    if (req.Coordinates == null || req.Coordinates.Length < 3)
    {
        logger.LogWarning("[GEOFENCE] Invalid geofence: fewer than 3 coordinates");
        return Results.BadRequest("Geofence requires at least 3 coordinates");
    }

    geofenceState.Boundary = req.Coordinates;
    // Geofence state already updated; publish is supplementary — don't fail the response on broker issues.
    publisher.PublishAsync("command.swarm.geofence", new AlertMessage("swarm", "geofence_deployed", req.Coordinates.Length, DateTime.UtcNow))
        .ContinueWith(t => logger.LogWarning("[GEOFENCE] Event publish failed: {Msg}", t.Exception!.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    logger.LogInformation("[GEOFENCE] Geofence deployed with {Count} vertices", req.Coordinates.Length);
    return Results.Ok(new { message = "Geofence deployed", vertexCount = req.Coordinates.Length });
});

app.MapPost("/admin/reset", async (
    IDbContextFactory<TelemetryDbContext> dbFactory,
    ConcurrentDictionary<string, C2Payload> commandQueue,
    GeofenceState geofenceState,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var deletedTelemetry = await db.Telemetry.ExecuteDeleteAsync(cancellationToken);

    commandQueue.Clear();
    geofenceState.Boundary = null;

    logger.LogWarning("[ADMIN] Database reset requested. Deleted {Count} telemetry rows and cleared command/geofence state.", deletedTelemetry);
    return Results.Ok(new { deletedTelemetry, clearedCommands = true, clearedGeofence = true });
});

app.MapPost("/telemetry", (TelemetryRecord record, Channel<TelemetryRecord> channel, ConcurrentDictionary<string, C2Payload> commandQueue, GeofenceState geofenceState, IRabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    if (!channel.Writer.TryWrite(record))
    {
        logger.LogWarning("Ingestion queue full. Dropping packet for {DeviceId}", record.DeviceId);
        return Results.StatusCode(503);
    }

    var droneId = record.DeviceId.ToString();
    IngestMetrics.TelemetryReceived.WithLabels(droneId).Inc();
    TelemetryProcessor.ChannelDepth.Inc();

    // Battery critical check (18V matches simulator threshold)
    const double BatteryCriticalV = 18.0;
    if (record.BatteryVoltage > 0 && record.BatteryVoltage <= BatteryCriticalV)
    {
        logger.LogWarning("[BATTERY CRITICAL] Drone {DeviceId} at {Voltage:F1}V. RTL engaged.", droneId, record.BatteryVoltage);
        publisher.PublishAsync("telemetry.alert.battery", new AlertMessage(droneId, "battery_critical", record.BatteryVoltage, DateTime.UtcNow))
            .ContinueWith(t => logger.LogWarning("[ALERT] Battery alert publish failed: {Msg}", t.Exception!.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        IngestMetrics.RtlTriggered.WithLabels("battery_critical").Inc();
        var batteryRtl = new C2Payload("RTL");
        commandQueue[droneId] = batteryRtl;
        return Results.Accepted("", batteryRtl);
    }

    // Geofence check — ray-casting
    if (geofenceState.Boundary != null && geofenceState.Boundary.Length >= 3)
    {
        var point = new Coordinate(record.Latitude, record.Longitude);
        bool isInsideGeofence = GeofenceHelper.IsPointInPolygon(point, geofenceState.Boundary);

        if (!isInsideGeofence)
        {
            logger.LogWarning("[GEOFENCE BREACH] Drone {DeviceId} exited boundary. RTL engaged.", droneId);
            publisher.PublishAsync("telemetry.alert.geofence", new AlertMessage(droneId, "geofence_breach", 0, DateTime.UtcNow))
                .ContinueWith(t => logger.LogWarning("[ALERT] Geofence alert publish failed: {Msg}", t.Exception!.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            IngestMetrics.GeofenceBreach.Inc();
            IngestMetrics.RtlTriggered.WithLabels("geofence").Inc();
            var rtlPayload = new C2Payload("RTL");
            commandQueue[droneId] = rtlPayload;
            return Results.Accepted("", rtlPayload);
        }
    }

    if (commandQueue.TryRemove(droneId, out var payload))
    {
        logger.LogInformation("Piggybacking '{Command}' onto ACK for {DeviceId}", payload.Command, record.DeviceId);
        return Results.Accepted("", payload);
    }

    return Results.Accepted();
}).RequireRateLimiting("telemetryPolicy");

app.Run();

// ==========================================
// 5. MODELS
// ==========================================
public record CommandRequest(string Command);

public record Coordinate(double Lat, double Lng);
public record SwarmRouteRequest(string[] DeviceIds, Coordinate[] Route);
public record C2Payload(string Command, object? Data = null);

public record GeofenceRequest(Coordinate[] Coordinates);

public class GeofenceState
{
    private readonly object _lock = new object();
    private Coordinate[]? _boundary;

    public Coordinate[]? Boundary
    {
        get { lock (_lock) { return _boundary; } }
        set { lock (_lock) { _boundary = value; } }
    }
}

internal static class IngestMetrics
{
    internal static readonly Counter TelemetryReceived = Metrics.CreateCounter(
        "aerostream_telemetry_received_total",
        "Telemetry packets accepted into the ingestion channel.",
        new CounterConfiguration { LabelNames = ["drone_id"] });

    internal static readonly Counter GeofenceBreach = Metrics.CreateCounter(
        "aerostream_geofence_breach_total",
        "Geofence breach events detected.");

    internal static readonly Counter RtlTriggered = Metrics.CreateCounter(
        "aerostream_rtl_triggered_total",
        "RTL commands triggered.",
        new CounterConfiguration { LabelNames = ["reason"] });
}

public static class GeofenceHelper
{
    public static bool IsPointInPolygon(Coordinate point, Coordinate[] polygon)
    {
        if (polygon.Length < 3) return false;

        int crossings = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Coordinate a = polygon[i];
            Coordinate b = polygon[(i + 1) % polygon.Length];

            if (IsRayIntersectingEdge(point, a, b))
                crossings++;
        }

        return crossings % 2 == 1;
    }

    private static bool IsRayIntersectingEdge(Coordinate point, Coordinate a, Coordinate b)
    {
        if ((a.Lat <= point.Lat && b.Lat > point.Lat) || (b.Lat <= point.Lat && a.Lat > point.Lat))
        {
            double xIntersect = a.Lng + (point.Lat - a.Lat) / (b.Lat - a.Lat) * (b.Lng - a.Lng);
            return point.Lng < xIntersect;
        }

        return false;
    }
}
