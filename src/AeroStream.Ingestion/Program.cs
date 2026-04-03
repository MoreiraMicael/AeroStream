using AeroStream.Ingestion;
using System.Threading.Channels;
using Serilog; 
using Scalar.AspNetCore; // CRITICAL: Missing this caused your CS1061 error
using Microsoft.EntityFrameworkCore; // Add this line!

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Serilog (Structured Logging)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AeroStream")
    .CreateLogger();

builder.Services.AddSerilog(); 

// 2. Register Services
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks(); // Standard for production monitoring
builder.Services.AddSignalR();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<TelemetryDbContext>(options =>
    options.UseNpgsql(connectionString));

// Add this in Section 2 (Services)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173") // No trailing slash!
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // MANDATORY for SignalR
    });
});

// 3. Register the Telemetry Pipeline
builder.Services.AddSingleton(Channel.CreateBounded<TelemetryRecord>(1000));
builder.Services.AddHostedService<TelemetryProcessor>();

var app = builder.Build();

// Order matters here!
app.UseCors(); // Must be BEFORE MapHub

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");
app.MapHub<TelemetryHub>("/telemetryHub");

// 5. The Ingestion Endpoint
app.MapPost("/telemetry", static (TelemetryRecord record, Channel<TelemetryRecord> channel, ILogger<Program> logger) =>
{
    if (channel.Writer.TryWrite(record))
    {
        logger.LogInformation("Accepted telemetry for device {DeviceId}", record.DeviceId);
        return Results.Accepted();
    }
    
    logger.LogWarning("Ingestion queue full. Dropping packet for {DeviceId}", record.DeviceId);
    return Results.StatusCode(503);
});

app.Run();