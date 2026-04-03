const crypto = require('crypto');

const API_URL = 'http://localhost:5233/telemetry';
const SWARM_SIZE = 10;
const FREQUENCY_MS = 100; // 10Hz

// Base Coordinates (Landing Zone)
const baseCoords = { lat: 39.586514, lng: -9.021444 };

// The Flight Plan (A rectangular patrol route around the AO)
const patrolRoute = [
    { lat: 39.5950, lng: -9.0250 }, // Waypoint 0: North-West
    { lat: 39.5950, lng: -9.0150 }, // Waypoint 1: North-East
    { lat: 39.5800, lng: -9.0150 }, // Waypoint 2: South-East
    { lat: 39.5800, lng: -9.0250 }  // Waypoint 3: South-West
];

// Calculate distance between two coordinates
const getDistance = (lat1, lng1, lat2, lng2) => {
    return Math.sqrt(Math.pow(lat2 - lat1, 2) + Math.pow(lng2 - lng1, 2));
};

// Initialize Swarm with absolute coordinates instead of offsets
const drones = Array.from({ length: SWARM_SIZE }).map((_, i) => {
    // Distribute the swarm across different starting waypoints
    const startPoint = patrolRoute[i % patrolRoute.length];
    return {
        id: crypto.randomUUID(),
        // Add a tiny random scatter so they don't fly exactly on top of each other
        lat: startPoint.lat + (Math.random() - 0.5) * 0.002, 
        lng: startPoint.lng + (Math.random() - 0.5) * 0.002,
        targetWaypointIndex: (i + 1) % patrolRoute.length, // Aim for the NEXT waypoint
        speed: 50 + (Math.random() * 30),
        altitude: 150 + (Math.random() * 20),
        mode: 'PATROL' 
    };
});

let successCount = 0;
let failCount = 0;

console.log(`🚁 Launching Waypoint Simulator...`);
console.log(`🎯 Target: ${API_URL}`);
console.log(`📍 Base: Valado dos Frades`);
console.log(`🛸 Drones: ${SWARM_SIZE}`);

setInterval(async () => {
    const promises = drones.map((drone, index) => {
        
        // --- 1. THROTTLE LOGIC FOR LANDED DRONES ---
        if (drone.mode === 'LANDED') {
            // Only ping the server once every 2 seconds (20 ticks of 100ms)
            drone.landedTick = (drone.landedTick || 0) + 1;
            if (drone.landedTick < 20) return Promise.resolve(); 
            drone.landedTick = 0; // Reset and allow one ping to go through
        }

        // --- 2. FLIGHT PHYSICS ---
        if (drone.mode === 'PATROL') {
            const target = patrolRoute[drone.targetWaypointIndex];
            const dist = getDistance(drone.lat, drone.lng, target.lat, target.lng);

            if (dist < 0.001) {
                drone.targetWaypointIndex = (drone.targetWaypointIndex + 1) % patrolRoute.length;
            }

            const stepSize = 0.00005 * (drone.speed / 50);
            drone.lat += ((target.lat - drone.lat) / dist) * stepSize;
            drone.lng += ((target.lng - drone.lng) / dist) * stepSize;
            
            drone.altitude = 150 + (Math.sin(Date.now() / 1000 + index) * 10);
            
        } else if (drone.mode === 'RTL') {
            const dist = getDistance(drone.lat, drone.lng, baseCoords.lat, baseCoords.lng);

            if (dist > 0.0005) { 
                // PHASE 1: FLY HOME (Maintain Altitude)
                const stepSize = 0.00008; 
                drone.lat += ((baseCoords.lat - drone.lat) / dist) * stepSize;
                drone.lng += ((baseCoords.lng - drone.lng) / dist) * stepSize;
                // Notice: No altitude reduction here. Maintain safe height.
            } else {
                // PHASE 2: VERTICAL DESCENT
                if (drone.altitude > 1) {
                    drone.altitude *= 0.85; // Drop fast
                } else {
                    drone.mode = 'LANDED';
                    drone.altitude = 0;
                    drone.speed = 0;
                    console.log(`\n✅ [DRONE ${drone.id.substring(0,6)}] TOUCHDOWN SECURED. SWITCHING TO HEARTBEAT MODE.`);
                }
            }
        }

        // --- 3. TELEMETRY ---
        const payload = {
            deviceId: drone.id,
            latitude: drone.lat,
            longitude: drone.lng,
            altitude: drone.altitude,
            speed: drone.speed,
            pitch: 0, roll: 0, yaw: 0,
            timestamp: new Date().toISOString()
        };

        return fetch(API_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(async res => {
            if (res.ok) {
                successCount++;
                const data = await res.json().catch(() => null);
                if (data && data.command === 'RTL' && drone.mode !== 'LANDED') {
                    console.log(`\n🚨 [DRONE ${drone.id.substring(0,6)}] RTL RECEIVED. ABORTING PATROL.`);
                    drone.mode = 'RTL';
                }
            } else failCount++;
        }).catch(() => failCount++);
    });

    await Promise.all(promises);
}, FREQUENCY_MS);

setInterval(() => {
    process.stdout.write(`\r📊 Throughput: ${successCount} pkt/sec | ❌ Dropped: ${failCount} | ⏱️ System nominal...`);
    successCount = 0; 
    failCount = 0;
}, 1000);