using AeroStream.Ingestion;
using System.Threading.Channels;
using Serilog; 
using Scalar.AspNetCore;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

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

var app = builder.Build();

app.UseCors();

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

// The Telemetry Endpoint
app.MapPost("/telemetry", (TelemetryRecord record, Channel<TelemetryRecord> channel, ConcurrentDictionary<string, C2Payload> commandQueue, ILogger<Program> logger) =>
{
    if (!channel.Writer.TryWrite(record))
    {
        logger.LogWarning("Ingestion queue full. Dropping packet for {DeviceId}", record.DeviceId);
        return Results.StatusCode(503);
    }
    
    // UPGRADE: We now extract the C2Payload object and return it directly
    if (commandQueue.TryRemove(record.DeviceId.ToString(), out var payload)) 
    {
        logger.LogInformation("Piggybacking '{Command}' onto ACK for {DeviceId}", payload.Command, record.DeviceId);
        // The framework will automatically serialize the payload (Command + Data) to JSON
        return Results.Accepted("", payload); 
    }

    return Results.Accepted(); 
});

app.Run();

// ==========================================
// 5. MODELS
// ==========================================
public record CommandRequest(string Command);

// NEW: Data structures for the dynamic routing
public record Coordinate(double Lat, double Lng);
public record SwarmRouteRequest(string[] DeviceIds, Coordinate[] Route);
public record C2Payload(string Command, object? Data = null);