using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AeroStream.Ingestion;

public class CommandDispatchConsumer(
    IConfiguration config,
    ConcurrentDictionary<string, C2Payload> commandQueue,
    ILogger<CommandDispatchConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IConnection? connection = null;
            IChannel? channel = null;

            try
            {
                connection = await BuildFactory().CreateConnectionAsync(stoppingToken);
                channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                await channel.ExchangeDeclareAsync("aerostream.events", ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync("command-dispatch-queue", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.QueueBindAsync("command-dispatch-queue", "aerostream.events", "command.#", cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync("telemetry-alert-queue", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.QueueBindAsync("telemetry-alert-queue", "aerostream.events", "telemetry.alert.#", cancellationToken: stoppingToken);

                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, cancellationToken: stoppingToken);

                // Command consumer — writes to in-memory commandQueue for drone ACK piggybacking.
                // Ack guarantee: survives API restart before consumption; a crash after ack but before
                // drone telemetry still loses the command (full fix = persist commandQueue to DB).
                var commandConsumer = new AsyncEventingBasicConsumer(channel);
                commandConsumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(ea.Body.Span);
                        ProcessMessage(json, commandQueue);
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("[MQ] Command message processing failed: {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                    }
                };
                await channel.BasicConsumeAsync("command-dispatch-queue", autoAck: false, consumer: commandConsumer, cancellationToken: stoppingToken);

                // Alert consumer — drains telemetry-alert-queue, writes to Serilog only.
                var alertConsumer = new AsyncEventingBasicConsumer(channel);
                alertConsumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(ea.Body.Span);
                        var alert = JsonSerializer.Deserialize<AlertMessage>(json, s_jsonOptions);
                        if (alert is not null)
                            logger.LogWarning("[ALERT] {AlertType} — Drone {DroneId} — Value: {Value:F1} at {Timestamp:O}",
                                alert.AlertType, alert.DroneId, alert.Value, alert.Timestamp);
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("[ALERT] Consumer failed: {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                    }
                };
                await channel.BasicConsumeAsync("telemetry-alert-queue", autoAck: false, consumer: alertConsumer, cancellationToken: stoppingToken);

                logger.LogInformation("CommandDispatchConsumer started. Consuming command-dispatch-queue and telemetry-alert-queue.");

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("CommandDispatchConsumer disconnected ({Msg}). Reconnecting in 3s...", ex.Message);
                await Task.Delay(3000, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                if (channel is not null) await channel.DisposeAsync();
                if (connection is not null) await connection.DisposeAsync();
            }
        }
    }

    // Extracted for unit testability — throws on bad JSON so the caller can nack.
    public static void ProcessMessage(string json, ConcurrentDictionary<string, C2Payload> commandQueue)
    {
        var msg = JsonSerializer.Deserialize<DispatchMessage>(json, s_jsonOptions);
        if (msg is not { DroneIds.Length: > 0 }) return;

        foreach (var droneId in msg.DroneIds)
            commandQueue[droneId] = new C2Payload(msg.Command, msg.Data);
    }

    private ConnectionFactory BuildFactory() => new()
    {
        HostName = config["RabbitMq:Host"] ?? "localhost",
        Port = int.TryParse(config["RabbitMq:Port"], out var p) ? p : 5672,
        UserName = config["RabbitMq:Username"] ?? "guest",
        Password = config["RabbitMq:Password"] ?? "guest",
    };
}
