# AeroStream

AeroStream is a containerized ground control and telemetry stack for simulated UAV swarms.

It ingests telemetry, persists it to PostgreSQL, broadcasts live updates over SignalR, and renders a tactical dashboard with route planning, geofencing, swarm commands, hunter/patrol roles, and simulator-driven validation.

## Current Stack

- Backend: .NET 10 Minimal API, SignalR, EF Core, Channels, Serilog
- Messaging: RabbitMQ 4 (topic exchange, durable queues, dead-letter exchange)
- Observability: Prometheus (metrics scrape) + Grafana (provisioned dashboard)
- Frontend: React 19, TypeScript, Vite, React-Leaflet
- Database: PostgreSQL 18
- Runtime: Docker Compose
- Simulator: Node.js script at [simulate.js](./simulate.js)

## What It Does

- Accepts high-frequency telemetry on `POST /telemetry`
- Queues telemetry through `System.Threading.Channels` before persistence
- Broadcasts live telemetry to the dashboard through SignalR
- Persists telemetry into PostgreSQL in batches
- Publishes battery-critical and geofence-breach alerts to RabbitMQ
- Routes operator commands through a durable RabbitMQ queue before drone delivery
- Supports swarm route deployment and geofence deployment
- Simulates `PATROL` and `HUNTER` drones with role-specific behavior
- Shows live drone state, trails, stale telemetry alerts, and battery state in the dashboard
- Allows operators to wipe persisted telemetry and reset command/geofence state from the dashboard
- Exposes Prometheus metrics at `/metrics`; Grafana dashboard auto-provisions on `docker compose up`

## Architecture

### Ingestion path

1. A drone or simulator sends telemetry to the API.
2. The API validates rate limits and enqueues telemetry immediately.
3. A background worker broadcasts the telemetry over SignalR.
4. The same worker persists telemetry to PostgreSQL in batches.
5. If the telemetry triggers a battery-critical or geofence-breach condition, the API publishes an alert to RabbitMQ (`telemetry.alert.battery` / `telemetry.alert.geofence`) and returns an RTL `C2Payload` in the 202 body.
6. Pending C2 commands are piggybacked back to the drone in the telemetry ACK.

### Control path

- Dashboard issues HTTP commands to the API.
- API publishes the command to a durable RabbitMQ topic exchange (`command.rtl`, `command.drone`, `command.swarm.route`).
- `CommandDispatchConsumer` (background service) consumes from `command-dispatch-queue` and writes the command to an in-memory `ConcurrentDictionary` keyed by drone ID.
- On the next telemetry ACK from that drone, the pending command is dequeued and returned in the 202 body.

The queue is durable: a restart of the API before consumption does not lose the command. The residual window — between queue consumption and drone delivery — is still in-memory only (see Current Limitations).

### Persistence model

- PostgreSQL runs in Docker as `aerostream-db`
- Persistent data is stored in the Docker volume `aerostream_pgdata`
- Database growth affects the volume, not the image size

## Services

Defined in [docker-compose.yml](./docker-compose.yml):

| Service | Port(s) | Notes |
|---------|---------|-------|
| `db` | 5432 | PostgreSQL |
| `rabbitmq` | 5672 (AMQP) / 15672 (management UI) | |
| `ingestion-api` | 5233 | Maps to container :8080 |
| `dashboard` | 5173 | Nginx in container |
| `prometheus` | 9090 | Scrapes `/metrics` every 5s |
| `grafana` | 3000 | 7-panel dashboard, auto-provisioned |

## Quick Start

### Run the full stack with Docker

From the repo root:

```bash
docker compose up --build -d
```

Open:

- Dashboard: `http://localhost:5173`
- API: `http://localhost:5233`
- RabbitMQ management UI: `http://localhost:15672` (guest / guest — localhost only)
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000` (admin / admin — see Operational Notes)

### Run the simulator

From the repo root:

```bash
node simulate.js
```

Current simulator defaults:

- Total drones: `5`
- Patrol: `4`
- Hunter: `1`

## Local Frontend Development

If you want hot reload instead of the Docker-served dashboard:

```bash
cd src/AeroStream.Dashboard
npm install
npm run dev
```

## Key Features

### Dashboard

- Live tactical map with drone markers and trails
- Smooth marker interpolation between telemetry updates
- Role-aware styling for `PATROL` and `HUNTER`
- Route drawing and swarm deployment
- Geofence drawing and deployment
- Per-drone `RTL`
- Swarm-wide `RTL ALL`
- Hunter activation and deactivation controls
- Stale drone detection
- Notification toasts for operator actions and link state
- `WIPE DB` button to clear persisted telemetry and reset backend runtime state

### Simulator

- Starts all drones `LANDED`
- Patrol drones require `UPDATE_ROUTE` before takeoff
- Hunter requires route assignment and `HUNTER_ON`
- Battery drain model with critical and ultra-critical behavior
- Emergency `RTL` on low battery
- In-place forced landing on ultra-critical battery
- Hunter tags patrol drones and forces them into `RTL`
- Sends per-drone telemetry rate-limit identity header for fair throttling

### Backend

- Per-drone telemetry rate limiting using request partitioning (20 req/s, queue 2)
- `UseHttpMetrics()` placed before `UseRateLimiter()` so 429 responses appear in Prometheus
- Geofence breach detection with immediate `RTL`
- Battery critical detection (≤18V) with immediate `RTL`
- Bounded ingestion channel (capacity 1000)
- Batched persistence to PostgreSQL (50 records or 500ms)
- SignalR real-time fanout
- RabbitMQ topic exchange for durable command dispatch and alert events
- Single-flight reconnect pattern — concurrent reconnect attempts collapse to one shared task
- Dead-letter exchange for poison messages; `JsonException` nacks with `requeue: false`
- Admin reset endpoint for telemetry wipe

### Observability

- 7 Prometheus metrics across 4 files (counters, gauges, histograms with label sets)
- Grafana dashboard auto-provisions on first `docker compose up` — no manual import
- 7 panels: per-drone ingest rate, channel depth / ingest throughput, batch latency by outcome, RTL triggers by reason, geofence breach rate, RabbitMQ publish vs consume, HTTP 429 rate

## Important Endpoints

- `POST /telemetry` — telemetry ingest (rate-limited per drone)
- `POST /command/{deviceId}` — single-drone command dispatch
- `POST /command/swarm/route` — swarm route update
- `POST /command/swarm/geofence` — geofence deployment
- `POST /admin/reset` — wipe telemetry + clear runtime state
- `GET /health` — RabbitMQ health check included
- `GET /metrics` — Prometheus metrics endpoint

## Operational Notes

### Credentials

**RabbitMQ management UI** — `http://localhost:15672` — default credentials are `guest` / `guest`. RabbitMQ restricts `guest` to localhost connections by default; this is safe for local development only. Change before any non-local exposure.

**Grafana** — `http://localhost:3000` — default credentials are `admin` / `admin`. Rotate before exposing beyond localhost. Set a new password via `GF_SECURITY_ADMIN_PASSWORD` in `docker-compose.yml` or via `docker exec aerostream-grafana grafana-cli admin reset-admin-password <new>`. The `GF_AUTH_ANONYMOUS_ENABLED=false` setting prevents unauthenticated access but does not substitute for a strong password.

### Wiping telemetry

You can wipe telemetry from the dashboard using the `WIPE DB` button.

This currently:

- Deletes all rows from `Telemetry`
- Clears queued commands
- Clears the active geofence
- Resets local dashboard state after the request succeeds

### Docker storage

- Postgres data lives in `aerostream_pgdata`
- Docker build cache can grow much faster than the DB volume
- Clearing Docker build cache does not wipe the database

Useful commands:

```bash
docker compose ps -a
docker volume ls
docker system df
docker builder prune -af
```

### Current database access

Local development uses the Docker Compose PostgreSQL service exposed on `localhost:5432`.

Credentials are defined in local runtime configuration and should be treated as development-only defaults. Do not publish or reuse them for any shared or production environment.

## Project Layout

- [simulate.js](./simulate.js): swarm simulator
- [docker-compose.yml](./docker-compose.yml): local runtime stack (6 services)
- [prometheus.yml](./prometheus.yml): Prometheus scrape config
- [grafana/provisioning](./grafana/provisioning): auto-provisioned datasource + dashboard
- [src/AeroStream.Ingestion](./src/AeroStream.Ingestion): backend API and persistence
- [src/AeroStream.Dashboard](./src/AeroStream.Dashboard): frontend dashboard
- [tests/AeroStream.Tests](./tests/AeroStream.Tests): test project

## Current Limitations

- Telemetry retention policy is not implemented yet
- Historical telemetry is append-only until wiped or manually pruned
- Commands consumed from RabbitMQ are held in-memory before drone delivery; a crash in that window still loses the command (full fix requires persisting pending commands to the database)
- Simulator physics are operationally useful but still simplified

## Recommended Next Steps

1. Add telemetry retention as a hosted background service
2. Batch SignalR broadcasts instead of sending one message per telemetry record
3. Add a latest-state table separate from historical telemetry
4. Reduce noisy info-level logging in hot backend paths
5. Persist consumed-but-undelivered commands to PostgreSQL to close the residual ack window between queue consumption and drone delivery

## Development Notes

This project was built and iterated using [Claude Code](https://claude.ai/code) as a structured agentic development tool — not as a code generator, but as a paired auditor.

Each of the three implementation phases (RabbitMQ, Prometheus, Grafana) followed the same loop:

1. **Implement** — Claude Code wrote the phase against a detailed spec, reasoning through architectural trade-offs inline
2. **Audit** — a separate audit prompt reviewed each item independently, requiring evidence (actual metric values, actual error messages, actual test output) rather than assertions
3. **Fix** — findings from the audit were fixed and re-verified against the original claim

This loop caught real bugs that would have passed a normal code review:

- **Silent-drop race**: the original reconnect logic used a `SemaphoreSlim` — concurrent callers who couldn't acquire it skipped reconnect silently, dropping messages with no error. Replaced with a single-flight `Task<bool>?` pattern.
- **Poison-message loop**: `JsonException` with `requeue: true` causes infinite requeue. Caught during audit; fixed with split catch blocks — `JsonException` → `nack(requeue: false)` → DLX, `Exception` → `nack(requeue: true)`.
- **Stale reconnect task**: the reconnect task was never nulled in the `finally` block, meaning a second `PublishAsync` after a transient failure would find the completed-but-failed task and throw instead of retrying. Audited via a live two-phase broker test.
- **Metric pipeline gap**: `UseRateLimiter()` was placed before `UseHttpMetrics()`, so 429 responses were rejected before reaching the metrics middleware and never appeared in Prometheus. Caught in the Phase 2 audit via a live `code="429"` label check.
- **Unverified vs verified**: the Phase 3 Grafana audit required actual PromQL results and triggered live events for each panel — not "metric exists in Prometheus." This caught a panel that was always flat-zero at normal load (channel depth gauge), which led to reworking it as a dual-series panel showing both ingest rate and backpressure depth.

The full audit trail — including specific PromQL queries run, counter values before/after each triggered event, latency numbers from the DB-kill failure test, and the reasoning behind each fix — lives in the PR description and conversation history. If you're reviewing this for an interview: the story is in the review loop, not the final diff.
