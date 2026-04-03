using AeroStream.Ingestion;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

// High-speed internal pipeline
builder.Services.AddSingleton(Channel.CreateBounded<TelemetryRecord>(1000));
builder.Services.AddHostedService<TelemetryProcessor>();

var app = builder.Build();

// Static lambda for zero-allocation routing
app.MapPost("/telemetry", static (TelemetryRecord record, Channel<TelemetryRecord> channel) =>
{
    return channel.Writer.TryWrite(record) ? Results.Accepted() : Results.StatusCode(503);
});

app.Run();