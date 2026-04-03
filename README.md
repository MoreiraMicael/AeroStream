# AeroStream: High-Throughput GCS Telemetry Engine

![AeroStream Dashboard](./docs/dashboard.png)

## Overview
AeroStream is a containerized, real-time Ground Control Station (GCS) telemetry ingestion engine. It is designed to handle high-frequency data streams from UAVs, persist them reliably to a relational database, and broadcast them to a React-based tactical dashboard with sub-second latency.

## Architecture & "The Why"
This system was built to solve the classic "Ingestion Bottleneck" found in IoT and Aerospace applications where data velocity often exceeds database write speeds.

* **The Problem:** High-frequency telemetry (10Hz+) can cause standard APIs to block during database latency spikes, leading to dropped packets and loss of situational awareness.
* **The AeroStream Solution:** 1. **Minimal API Gateway:** Receives telemetry and immediately offloads it to an in-memory `System.Threading.Channels` buffer, returning `202 Accepted` in < 2ms.
    2. **Asynchronous Background Worker:** A dedicated consumer processes the queue, decoupling the ingestion rate from the persistence layer.
    3. **Real-Time Propagation:** The processor simultaneously broadcasts data via **SignalR WebSockets** for live UI updates while committing the record to **PostgreSQL** for permanent "Black Box" flight logging.

## Tech Stack
* **Backend:** .NET 10 (Minimal APIs, Channels, SignalR, EF Core)
* **Frontend:** React 19, TypeScript, Vite, React-Leaflet (CartoDB Tactical Maps)
* **Database:** PostgreSQL 18
* **Infrastructure:** Docker Compose (Persistent Volumes, Multi-container Networking)

## Quick Start (Flight Simulator)

To spin up the entire infrastructure locally, execute the following in separate terminal windows:

### 1. Start the Backend & Database (Docker)
```bash
docker compose up --build
cd src/AeroStream.Dashboard
npm install
npm run dev

# Run from the root directory
node simulate.js

# Run from the root directory
node simulate.js