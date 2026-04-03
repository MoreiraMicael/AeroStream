using AeroStream.Ingestion;
using System.Threading.Channels;
using Serilog; 
using Scalar.AspNetCore; // CRITICAL: Missing this caused your CS1061 error

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

// 3. Register the Telemetry Pipeline
builder.Services.AddSingleton(Channel.CreateBounded<TelemetryRecord>(1000));
builder.Services.AddHostedService<TelemetryProcessor>();

var app = builder.Build();

// 4. Configure Middleware
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); 
    app.MapScalarApiReference(); // Now this will work
}

app.MapHub<TelemetryHub>("/telemetryHub");
app.MapHealthChecks("/health"); // Essential for Docker/Kubernetes

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