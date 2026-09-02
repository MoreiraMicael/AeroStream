using AeroStream.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AeroStream.Tests;

public class CommandDispatchConsumerTests
{
    [Fact]
    public void ProcessMessage_SingleDrone_WritesCommandToQueue()
    {
        var queue = new ConcurrentDictionary<string, C2Payload>();

        CommandDispatchConsumer.ProcessMessage(
            """{"droneIds":["drone-abc"],"command":"RTL","data":null}""",
            queue);

        Assert.True(queue.ContainsKey("drone-abc"));
        Assert.Equal("RTL", queue["drone-abc"].Command);
    }

    [Fact]
    public void ProcessMessage_MultiDrone_WritesAllDrones()
    {
        var queue = new ConcurrentDictionary<string, C2Payload>();

        CommandDispatchConsumer.ProcessMessage(
            """{"droneIds":["d1","d2","d3"],"command":"UPDATE_ROUTE","data":null}""",
            queue);

        Assert.Equal(3, queue.Count);
        Assert.All(queue.Values, p => Assert.Equal("UPDATE_ROUTE", p.Command));
    }

    [Fact]
    public void ProcessMessage_EmptyDroneIds_WritesNothing()
    {
        var queue = new ConcurrentDictionary<string, C2Payload>();

        CommandDispatchConsumer.ProcessMessage(
            """{"droneIds":[],"command":"RTL","data":null}""",
            queue);

        Assert.Empty(queue);
    }

    [Fact]
    public void ProcessMessage_InvalidJson_Throws()
    {
        // The consumer catches this and nacks with requeue=true for redelivery.
        var queue = new ConcurrentDictionary<string, C2Payload>();
        Assert.ThrowsAny<Exception>(() =>
            CommandDispatchConsumer.ProcessMessage("not-valid-json", queue));
    }

    [Fact]
    public void ProcessMessage_OverwritesExistingCommand()
    {
        var queue = new ConcurrentDictionary<string, C2Payload>();
        queue["drone-1"] = new C2Payload("OLD_COMMAND");

        CommandDispatchConsumer.ProcessMessage(
            """{"droneIds":["drone-1"],"command":"RTL","data":null}""",
            queue);

        Assert.Equal("RTL", queue["drone-1"].Command);
    }
}

public class RabbitMqPublisherTests
{
    [Fact]
    public async Task PublishAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        // Port 1 → immediate "connection refused" rather than a long DNS/TCP timeout.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "127.0.0.1",
                ["RabbitMq:Port"] = "1",
            })
            .Build();

        await using var publisher = new RabbitMqPublisher(config, NullLogger<RabbitMqPublisher>.Instance);
        // StartAsync not called — publisher is disconnected.

        // Publisher must throw so callers (endpoints) can return 503 instead of silently dropping.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync("test.key", new { x = 1 }));
        Assert.False(publisher.IsConnected);
    }

    [Fact]
    public async Task PublishAsync_AfterFailedReconnect_SucceedsWhenBrokerAvailable()
    {
        // Simulates a broker restart. Without the finally-block null-clear in DoReconnectAsync,
        // _reconnectTask would remain a completed Task<bool>(false) after the first failure.
        // The second call would hit ??= with a non-null task, skip creating a new one, and
        // throw immediately — never attempting TCP even though the broker is now reachable.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "127.0.0.1",
                ["RabbitMq:Port"] = "1",
            })
            .Build();

        await using var publisher = new RabbitMqPublisher(config, NullLogger<RabbitMqPublisher>.Instance);

        // Phase 1: broker unreachable — must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync("test.refused", new { x = 1 }));
        Assert.False(publisher.IsConnected);

        // "Broker restarts": update config to point at live RabbitMQ.
        // IConfiguration.set propagates through ConfigurationRoot → MemoryConfigurationProvider.Data,
        // so the next BuildFactory() call inside DoReconnectAsync reads the new values.
        config["RabbitMq:Host"] = "localhost";
        config["RabbitMq:Port"] = "5672";

        // Phase 2: broker reachable — must not throw.
        await publisher.PublishAsync("test.live", new { x = 2 });
        Assert.True(publisher.IsConnected);
    }
}

public class RabbitMqHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_WhenConnected_ReturnsHealthy()
    {
        var result = await new RabbitMqHealthCheck(new FakePublisher(isConnected: true))
            .CheckHealthAsync(null!);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealth_WhenDisconnected_ReturnsUnhealthy()
    {
        var result = await new RabbitMqHealthCheck(new FakePublisher(isConnected: false))
            .CheckHealthAsync(null!);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class FakePublisher(bool isConnected) : IRabbitMqPublisher
    {
        public bool IsConnected => isConnected;
        public Task PublishAsync(string routingKey, object message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
