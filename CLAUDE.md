# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Full stack
```bash
docker compose up --build -d   # build + start all services
docker compose ps -a           # check service health
node simulate.js               # run the swarm simulator (5 drones: 4 patrol, 1 hunter)
```

### Backend (.NET 10)
```bash
dotnet build                                          # build solution
dotnet run --project src/AeroStream.Ingestion         # run API locally (no Docker)
dotnet test                                           # run all tests
dotnet test --filter "FullyQualifiedName~ClassName"   # run single test class
```

### Frontend (React + Vite)
```bash
cd src/AeroStream.Dashboard
npm install
npm run dev      # hot-reload dev server on :5173
npm run build    # production build
npm run lint     # ESLint
```

### Useful Docker ops
```bash
docker builder prune -af    # clear build cache (does NOT wipe DB volume)
docker volume ls            # aerostream_pgdata holds all telemetry
```

## Architecture

### Data flow (hot path)
```
simulate.js  →  POST /telemetry  →  System.Threading.Channel (bounded, 1000)
                                           ↓
                                  TelemetryProcessor (BackgroundService)
                                      ├── SignalR broadcast (ReceiveTelemetry)
                                      └── batched EF Core insert (50 records or 500ms)
```

### C2 command path (piggyback pattern)
Commands are stored in `ConcurrentDictionary<string, C2Payload>` keyed by droneId. On the next `POST /telemetry` from that drone, the pending payload is dequeued and returned in the HTTP 202 body — the drone reads its own ACK. This is in-memory only (known limitation, `IMPLEMENTATION_PLAN.md` plans RabbitMQ to fix this).

### Geofence enforcement
`GeofenceState` (singleton, thread-safe) holds the active polygon. Every `POST /telemetry` runs a ray-casting point-in-polygon check (`GeofenceHelper.IsPointInPolygon`). On breach, an RTL `C2Payload` is inserted into the command dict and returned immediately without waiting for the next telemetry cycle.

### Rate limiting
Per-drone fixed-window limiter partitioned on `X-Drone-Id` header (20 req/s, queue 2). Falls back to remote IP if header is absent. Applied only to `POST /telemetry`.

### Frontend state model
`App.tsx` is a single large component. Drone state lives in refs (`dronesRef`, `prevDronesRef`, `pathsRef`) to avoid re-renders on every telemetry packet. A 100ms `setInterval` snapshots refs into React state for rendering. `AnimatedDroneMarkers` runs a `requestAnimationFrame` loop that interpolates between the previous and current position for smooth marker movement.

## Key files

| File | Role |
|------|------|
| `src/AeroStream.Ingestion/Program.cs` | All API endpoints + models (monolithic minimal API) |
| `src/AeroStream.Ingestion/TelemetryProcessor.cs` | Background service: SignalR fanout + batch persistence |
| `src/AeroStream.Ingestion/TelemetryRecord.cs` | Core telemetry model (C# 14 `field` keyword for altitude clamp) |
| `src/AeroStream.Ingestion/TelemetryDbContext.cs` | EF Core context (single `Telemetry` table) |
| `src/AeroStream.Dashboard/src/App.tsx` | Entire frontend |
| `simulate.js` | Drone swarm simulator with battery model, hunter/patrol roles, MSP protocol stub |
| `docker-compose.yml` | 3 services: `db` (Postgres :5432), `ingestion-api` (:5233→8080), `dashboard` (:5173→80) |

## Planned work (IMPLEMENTATION_PLAN.md)
Three phases in strict order:
1. **RabbitMQ** — replace in-memory command dict with durable queue; publish geofence/battery alerts to topic exchange
2. **Prometheus** — add `prometheus-net.AspNetCore`, expose `/metrics`, instrument key counters/histograms
3. **Grafana** — provision Prometheus datasource + dashboard panels; `docker compose up` should bring all 6 services healthy

Do phases in order — Phase 2 instruments the RabbitMQ code path added in Phase 1.

## Service ports

| Service | Local port | Notes |
|---------|-----------|-------|
| PostgreSQL | 5432 | `aerostream_pgdata` volume |
| ingestion-api | 5233 | Maps to container :8080; Scalar UI at `/scalar/v1` in dev |
| dashboard | 5173 | Nginx in container |
| RabbitMQ (planned) | 5672 / 15672 | AMQP / management UI |
| Prometheus (planned) | 9090 | |
| Grafana (planned) | 3000 | |

## Environment / config

- `AEROSTREAM_DB_PASSWORD` env var overrides the default `local-dev-password` in `docker-compose.yml`
- `VITE_API_BASE_URL` controls the API endpoint the frontend targets (default `http://localhost:5233`)
- Backend DB connection string: `ConnectionStrings__DefaultConnection` (set via compose env)
- `TelemetryProcessor` retries DB connection on startup with 2s backoff — safe to start before Postgres is ready
