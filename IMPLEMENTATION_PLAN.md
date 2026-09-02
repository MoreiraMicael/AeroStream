# AeroStream — Messaging & Observability Implementation Plan

Goal: close the two remaining gaps against the Tekever GCS job description —
durable message-queue experience (RabbitMQ) and a metrics/observability stack
(Prometheus + Grafana) — by extending the existing AeroStream backend rather
than starting a new project.

Do the phases in order. Each is independently testable before moving on.

---

## Phase 1 — RabbitMQ (durable event queue + command dispatch)

**Why:** Currently, pending commands live in an in-memory dictionary and
alerts (geofence breach, battery critical) trigger logic inline. This is the
piece the JD calls "messaging systems / event-driven workflows," and it also
fixes AeroStream's own noted limitation: "Commands are stored in memory, not
durably queued."

**1.1 Infrastructure**
- Add to `docker-compose.yml`:
  ```yaml
  rabbitmq:
    image: rabbitmq:3-management
    ports:
      - "5672:5672"   # AMQP
      - "15672:15672" # management UI
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "check_port_connectivity"]
      interval: 10s
      timeout: 5s
      retries: 5
  ```
- Add `depends_on: rabbitmq (healthy)` to `ingestion-api`.

**1.2 Package**
- `AeroStream.Ingestion`: add `RabbitMQ.Client` (or `MassTransit` +
  `MassTransit.RabbitMQ` if you want higher-level pub/sub abstractions —
  MassTransit is the more "senior" choice to show on a CV, plain
  `RabbitMQ.Client` is faster to implement).

**1.3 Topology**
- One topic exchange: `aerostream.events`
- Routing keys: `telemetry.alert.geofence`, `telemetry.alert.battery`,
  `command.rtl`, `command.swarm.route`, `command.swarm.geofence`
- One durable queue per consumer group, e.g. `command-dispatch-queue`
  bound to `command.*`.

**1.4 Code changes**
- Geofence breach detection and battery-critical logic: replace direct
  in-memory RTL trigger with **publish** to `telemetry.alert.*`.
- New `CommandDispatchConsumer` (hosted background service): consumes
  `command.*`, writes into the existing per-drone pending-command store
  that's piggybacked on telemetry ACK. This makes the command path durable
  without changing the drone-facing ACK protocol.
- `GET /health`: extend to check RabbitMQ connection state.

**1.5 Verify**
- Kill and restart `ingestion-api` mid-flight — pending commands should
  survive because they're now sourced from the queue, not just memory.
- RabbitMQ management UI (`localhost:15672`) shows message flow live —
  good demo material for interviews.

---

## Phase 2 — Prometheus (metrics)

**Why:** You have Serilog (logs) but no metrics. The JD explicitly lists
Prometheus/Grafana under observability.

**2.1 Package**
- `AeroStream.Ingestion`: add `prometheus-net.AspNetCore`.

**2.2 Program.cs**
```csharp
app.UseHttpMetrics();
app.MapMetrics(); // exposes /metrics
```

**2.3 Custom metrics to add**
- `Counter`  telemetry_received_total (label: droneId)
- `Gauge`    ingestion_channel_depth
- `Histogram` persistence_batch_duration_seconds
- `Counter`  geofence_breach_total
- `Counter`  rtl_triggered_total (label: reason)
- `Counter`  rabbitmq_messages_published_total / consumed_total

**2.4 Infrastructure**
- Add `prometheus` service to `docker-compose.yml` (image `prom/prometheus`),
  mount a `prometheus.yml` scrape config pointing at
  `ingestion-api:<port>/metrics` every 5–10s.

---

## Phase 3 — Grafana (dashboard)

**Why:** Turns Phase 2's raw metrics into something you can screen-share in
an interview.

**3.1 Infrastructure**
- Add `grafana` service (image `grafana/grafana`), port `3000`.
- Provision the Prometheus datasource via a mounted
  `provisioning/datasources/prometheus.yml` (avoids manual UI setup, keeps
  it reproducible from `docker compose up`).

**3.2 Dashboard panels**
- Telemetry ingest rate (per drone, stacked)
- Ingestion channel depth over time (shows backpressure behavior)
- Persistence batch latency (p50/p95)
- Geofence breaches / RTL triggers over time
- RabbitMQ publish vs consume rate (queue health)

---

## Sequencing

1→2→3, strictly. Phase 1 changes application logic (command dispatch path)
so it needs to land and be stable before you start instrumenting it in
Phase 2. Phase 3 is pure visualization on top of Phase 2 and can't start
before it.

## Definition of done

- `docker compose up --build -d` brings up 6 healthy services: db,
  ingestion-api, dashboard, rabbitmq, prometheus, grafana.
- Killing `ingestion-api` mid-session and restarting it does not lose
  pending commands (proves durability).
- Grafana dashboard shows live data while `simulate.js` is running.
- README updated: new services, new endpoints/ports, updated architecture
  diagram description (ingestion path now includes RabbitMQ hop).
