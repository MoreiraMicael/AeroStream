using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Prometheus;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AeroStream.Ingestion;

public class CommandDispatchConsumer(
    IConfiguration config,
    ConcurrentDictionary<string, C2Payload> commandQueue,
    ILogger<CommandDispatchConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Counter s_consumed = Metrics.CreateCounter(
        "aerostream_rabbitmq_consumed_total",
        "Messages consumed from RabbitMQ queues.",
        new CounterConfiguration { LabelNames = ["queue", "result"] });
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

                // Dead-letter exchange: nacked messages with requeue:false route here instead of vanishing.
                await channel.ExchangeDeclareAsync("aerostream.dlx", ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync("dead-letter-queue", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.QueueBindAsync("dead-letter-queue", "aerostream.dlx", routingKey: "", cancellationToken: stoppingToken);

                // x-dead-letter-exchange routes permanently-failed messages to aerostream.dlx
                // instead of dropping them silently.
                var dlxArgs = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "aerostream.dlx" };

                await channel.QueueDeclareAsync("command-dispatch-queue", durable: true, exclusive: false, autoDelete: false, arguments: dlxArgs, cancellationToken: stoppingToken);
                await channel.QueueBindAsync("command-dispatch-queue", "aerostream.events", "command.#", cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync("telemetry-alert-queue", durable: true, exclusive: false, autoDelete: false, arguments: dlxArgs, cancellationToken: stoppingToken);
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
                        s_consumed.WithLabels("command-dispatch-queue", "ack").Inc();
                    }
                    catch (JsonException ex)
                    {
                        // Permanent failure — malformed JSON will never parse correctly.
                        // nack + requeue:false → DLX captures it; no infinite retry loop.
                        logger.LogError("[MQ] Malformed command message (→ DLQ): {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        s_consumed.WithLabels("command-dispatch-queue", "nack_dead_letter").Inc();
                    }
                    catch (Exception ex)
                    {
                        // Transient failure (DB down, timeout, etc.) — safe to retry.
                        logger.LogError("[MQ] Transient command processing error (requeue): {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                        s_consumed.WithLabels("command-dispatch-queue", "nack_requeue").Inc();
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
                        s_consumed.WithLabels("telemetry-alert-queue", "ack").Inc();
                    }
                    catch (JsonException ex)
                    {
                        logger.LogError("[ALERT] Malformed alert message (→ DLQ): {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        s_consumed.WithLabels("telemetry-alert-queue", "nack_dead_letter").Inc();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("[ALERT] Transient consumer error (requeue): {Msg}", ex.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                        s_consumed.WithLabels("telemetry-alert-queue", "nack_requeue").Inc();
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
