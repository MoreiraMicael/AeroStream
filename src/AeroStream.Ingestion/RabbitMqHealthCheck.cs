using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AeroStream.Ingestion;

public class RabbitMqHealthCheck(IRabbitMqPublisher publisher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(publisher.IsConnected
            ? HealthCheckResult.Healthy("RabbitMQ connected.")
            : HealthCheckResult.Unhealthy("RabbitMQ disconnected."));
}
