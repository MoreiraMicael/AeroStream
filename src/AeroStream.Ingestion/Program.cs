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

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<TelemetryDbContext>(options =>
    options.UseNpgsql(connectionString));

// CORS - Unified into a single, secure SignalR-compatible policy
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173") // Your React app
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Required for SignalR
    });
});

// Telemetry Pipeline & C2 State
builder.Services.AddSingleton(Channel.CreateBounded<TelemetryRecord>(1000));
builder.Services.AddHostedService<TelemetryProcessor>();

// NEW: Register the Command Buffer into DI so any class/endpoint can access it
builder.Services.AddSingleton<ConcurrentDictionary<string, string>>();


// ==========================================
// 3. HTTP PIPELINE
// ==========================================
var app = builder.Build();

app.UseCors(); // Must remain before MapHub

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

// The C2 Endpoint: Queues commands from the React Dashboard
app.MapPost("/command/{deviceId}", (string deviceId, CommandRequest req, ConcurrentDictionary<string, string> commandQueue, ILogger<Program> logger) => 
{
    commandQueue[deviceId] = req.Command;
    logger.LogInformation("[C2] Command '{Command}' queued for Drone {DeviceId}", req.Command, deviceId);
    return Results.Ok();
});

// The Telemetry Endpoint: Ingests data and piggybacks pending C2 commands
app.MapPost("/telemetry", (TelemetryRecord record, Channel<TelemetryRecord> channel, ConcurrentDictionary<string, string> commandQueue, ILogger<Program> logger) =>
{
    // 1. Try to push telemetry to the background worker (Synchronous, non-blocking)
    if (!channel.Writer.TryWrite(record))
    {
        logger.LogWarning("Ingestion queue full. Dropping packet for {DeviceId}", record.DeviceId);
        return Results.StatusCode(503);
    }
    
// 2. Check if the commander issued an order for this specific drone
    // Fix: Convert the Guid DeviceId to a string to match the dictionary key
    if (commandQueue.TryRemove(record.DeviceId.ToString(), out var c2Command)) 
    {
        logger.LogInformation("Piggybacking '{Command}' onto ACK for {DeviceId}", c2Command, record.DeviceId);
        return Results.Accepted("", new { Command = c2Command });
    }

    return Results.Accepted(); // Normal ACK
});

app.Run();

// ==========================================
// 5. MODELS
// ==========================================
public record CommandRequest(string Command);