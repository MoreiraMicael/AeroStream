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
    public async Task PublishAsync_WhenNotConnected_DoesNotThrow()
    {
        // Publisher starts disconnected (StartAsync never called).
        // Verifies silent degrade: logs warning, returns, does not throw.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RabbitMq:Host"] = "nonexistent-host" })
            .Build();

        await using var publisher = new RabbitMqPublisher(config, NullLogger<RabbitMqPublisher>.Instance);

        var ex = await Record.ExceptionAsync(() => publisher.PublishAsync("test.key", new { x = 1 }));

        Assert.Null(ex);
        Assert.False(publisher.IsConnected);
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
