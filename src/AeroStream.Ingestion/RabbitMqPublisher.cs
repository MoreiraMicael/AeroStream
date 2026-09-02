using System.Text.Json;
using Prometheus;
using RabbitMQ.Client;

namespace AeroStream.Ingestion;

public interface IRabbitMqPublisher
{
    /// <summary>
    /// Publishes a message. Throws <see cref="InvalidOperationException"/> if RabbitMQ is
    /// unavailable after a reconnect attempt, so callers can return an appropriate error response.
    /// </summary>
    Task PublishAsync(string routingKey, object message, CancellationToken ct = default);
    bool IsConnected { get; }
}

public sealed class RabbitMqPublisher(IConfiguration config, ILogger<RabbitMqPublisher> logger)
    : IRabbitMqPublisher, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Counter s_published = Metrics.CreateCounter(
        "aerostream_rabbitmq_published_total",
        "Messages successfully published to RabbitMQ.",
        new CounterConfiguration { LabelNames = ["routing_key"] });

    private IConnection? _connection;
    private IChannel? _channel;
    private volatile bool _ready;

    // Single-flight reconnect: all concurrent callers await the same Task<bool>.
    // Whoever arrives first creates it; the rest await the cached reference and get the real outcome.
    private readonly object _taskSync = new();
    private Task<bool>? _reconnectTask;

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
            Task<bool> reconnect;
            lock (_taskSync)
            {
                // ??= means only the first caller creates the task; everyone else gets the same one.
                _reconnectTask ??= DoReconnectAsync();
                reconnect = _reconnectTask;
            }

            bool connected = await reconnect;
            if (!connected)
                throw new InvalidOperationException($"RabbitMQ unavailable. Command not published: {routingKey}");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(message, s_jsonOptions);
        try
        {
            await _channel!.BasicPublishAsync(
                exchange: "aerostream.events",
                routingKey: routingKey,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = true },
                body: body,
                cancellationToken: ct);
            s_published.WithLabels(routingKey).Inc();
        }
        catch (Exception ex)
        {
            // Connection dropped mid-publish; mark disconnected so the next call triggers reconnect.
            _ready = false;
            throw new InvalidOperationException($"RabbitMQ publish failed: {ex.Message}", ex);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _ready = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _ready = false;
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

    // Uses CancellationToken.None: reconnect is a service-level operation, not tied to any
    // individual request's cancellation token. A request cancelling shouldn't abort a reconnect
    // that other concurrent callers are also waiting on.
    private async Task<bool> DoReconnectAsync()
    {
        try
        {
            _ready = false;
            if (_channel is not null) { await _channel.DisposeAsync(); _channel = null; }
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            await ConnectAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("RabbitMQ publisher reconnect failed: {Msg}", ex.Message);
            return false;
        }
        finally
        {
            // Clear the cached task so the next failure triggers a fresh reconnect attempt.
            lock (_taskSync) { _reconnectTask = null; }
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
