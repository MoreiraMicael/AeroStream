using System.Text.Json;
using RabbitMQ.Client;

namespace AeroStream.Ingestion;

public interface IRabbitMqPublisher
{
    Task PublishAsync(string routingKey, object message, CancellationToken ct = default);
    bool IsConnected { get; }
}

public sealed class RabbitMqPublisher(IConfiguration config, ILogger<RabbitMqPublisher> logger)
    : IRabbitMqPublisher, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _ready;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);

    public bool IsConnected => _ready;

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("RabbitMQ publisher not ready ({Msg}). Retrying in 3s...", ex.Message);
                await Task.Delay(3000, ct);
            }
        }
    }

    public async Task PublishAsync(string routingKey, object message, CancellationToken ct = default)
    {
        if (!_ready || _channel is null)
        {
            // One reconnect attempt at a time; concurrent callers skip and log.
            if (await _reconnectGate.WaitAsync(0))
            {
                try { await TryReconnectAsync(ct); }
                finally { _reconnectGate.Release(); }
            }

            if (!_ready)
            {
                logger.LogWarning("RabbitMQ publish skipped (not connected). Key={Key}", routingKey);
                return;
            }
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(message, s_jsonOptions);
        await _channel!.BasicPublishAsync(
            exchange: "aerostream.events",
            routingKey: routingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: body,
            cancellationToken: ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _ready = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _ready = false;
        _reconnectGate.Dispose();
        if (_channel is not null) { await _channel.DisposeAsync(); _channel = null; }
        if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        _connection = await BuildFactory().CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync(
            exchange: "aerostream.events",
            type: ExchangeType.Topic,
            durable: true,
            cancellationToken: ct);
        _ready = true;
        logger.LogInformation("RabbitMQ publisher connected to {Host}.", config["RabbitMq:Host"]);
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        try
        {
            _ready = false;
            if (_channel is not null) { await _channel.DisposeAsync(); _channel = null; }
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            await ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("RabbitMQ publisher reconnect failed: {Msg}", ex.Message);
        }
    }

    private ConnectionFactory BuildFactory() => new()
    {
        HostName = config["RabbitMq:Host"] ?? "localhost",
        Port = int.TryParse(config["RabbitMq:Port"], out var p) ? p : 5672,
        UserName = config["RabbitMq:Username"] ?? "guest",
        Password = config["RabbitMq:Password"] ?? "guest",
    };
}
