# AeroStream

AeroStream is a containerized ground control and telemetry stack for simulated UAV swarms.

It ingests telemetry, persists it to PostgreSQL, broadcasts live updates over SignalR, and renders a tactical dashboard with route planning, geofencing, swarm commands, hunter/patrol roles, and simulator-driven validation.

## Current Stack

- Backend: .NET 10 Minimal API, SignalR, EF Core, Channels, Serilog
- Frontend: React 19, TypeScript, Vite, React-Leaflet
- Database: PostgreSQL 18
- Runtime: Docker Compose
- Simulator: Node.js script at [simulate.js](./simulate.js)

## What It Does

- Accepts high-frequency telemetry on `POST /telemetry`
- Queues telemetry through `System.Threading.Channels` before persistence
- Broadcasts live telemetry to the dashboard through SignalR
- Persists telemetry into PostgreSQL in batches
- Supports per-drone commands like `RTL`
- Supports swarm route deployment and geofence deployment
- Simulates `PATROL` and `HUNTER` drones with role-specific behavior
- Shows live drone state, trails, stale telemetry alerts, and battery state in the dashboard
- Allows operators to wipe persisted telemetry and reset command/geofence state from the dashboard

## Architecture

### Ingestion path

1. A drone or simulator sends telemetry to the API.
2. The API validates rate limits and enqueues telemetry immediately.
3. A background worker broadcasts the telemetry over SignalR.
4. The same worker persists telemetry to PostgreSQL in batches.
5. Pending C2 commands are piggybacked back to the drone in the telemetry ACK.

### Control path

- Dashboard issues HTTP commands to the API.
- API stores pending commands in memory per drone.
- On the next telemetry ACK, the drone receives the command payload.

### Persistence model

- PostgreSQL runs in Docker as `aerostream-db`
- Persistent data is stored in the Docker volume `aerostream_pgdata`
- Database growth affects the volume, not the image size

## Services

Defined in [docker-compose.yml](./docker-compose.yml):

- `db`: PostgreSQL on `localhost:5432`
- `ingestion-api`: backend API on `localhost:5233`
- `dashboard`: built frontend served on `localhost:5173`

## Quick Start

### Run the full stack with Docker

From the repo root:

```bash
docker compose up --build -d
```

Open:

- Dashboard: `http://localhost:5173`
- API: `http://localhost:5233`

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

- Per-drone telemetry rate limiting using request partitioning
- Geofence breach detection with immediate `RTL`
- Bounded ingestion channel
- Batched persistence to PostgreSQL
- SignalR real-time fanout
- Admin reset endpoint for telemetry wipe

## Important Endpoints

- `POST /telemetry`
- `POST /command/{deviceId}`
- `POST /command/swarm/route`
- `POST /command/swarm/geofence`
- `POST /admin/reset`
- `GET /health`
- SignalR hub: `/telemetryHub`

## Operational Notes

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
- [docker-compose.yml](./docker-compose.yml): local runtime stack
- [src/AeroStream.Ingestion](./src/AeroStream.Ingestion): backend API and persistence
- [src/AeroStream.Dashboard](./src/AeroStream.Dashboard): frontend dashboard
- [tests/AeroStream.Tests](./tests/AeroStream.Tests): test project

## Current Limitations

- Telemetry retention policy is not implemented yet
- Historical telemetry is append-only until wiped or manually pruned
- Commands are stored in memory, not durably queued
- Simulator physics are operationally useful but still simplified

## Recommended Next Steps

1. Add telemetry retention as a hosted background service
2. Batch SignalR broadcasts instead of sending one message per telemetry record
3. Add a latest-state table separate from historical telemetry
4. Reduce noisy info-level logging in hot backend paths