const crypto = require('crypto');

const API_URL = 'http://localhost:5233/telemetry';
const SWARM_SIZE = 10;
const FREQUENCY_MS = 100;

const baseCoords = { lat: 39.586514, lng: -9.021444 };

// Default flight plan (Valado dos Frades)
const defaultRoute = [
    { lat: 39.5950, lng: -9.0250 },
    { lat: 39.5950, lng: -9.0150 },
    { lat: 39.5800, lng: -9.0150 },
    { lat: 39.5800, lng: -9.0250 }
];

const getDistance = (lat1, lng1, lat2, lng2) => {
    return Math.sqrt(Math.pow(lat2 - lat1, 2) + Math.pow(lng2 - lng1, 2));
};

// Initialize Swarm
const drones = Array.from({ length: SWARM_SIZE }).map((_, i) => {
    const startPoint = defaultRoute[i % defaultRoute.length];
    return {
        id: crypto.randomUUID(),
        lat: startPoint.lat + (Math.random() - 0.5) * 0.002, 
        lng: startPoint.lng + (Math.random() - 0.5) * 0.002,
        patrolRoute: [...defaultRoute], // UPGRADE: Each drone holds its own active route
        targetWaypointIndex: (i + 1) % defaultRoute.length,
        speed: 50 + (Math.random() * 30),
        altitude: 150 + (Math.random() * 20),
        mode: 'PATROL' 
    };
});

let successCount = 0;
let failCount = 0;

console.log(`🚁 Launching Dynamic Route Simulator...`);
console.log(`🎯 Target: ${API_URL}`);
console.log(`🛸 Drones: ${SWARM_SIZE}`);

setInterval(async () => {
    const promises = drones.map((drone, index) => {
        
        // --- 1. THROTTLE LOGIC FOR LANDED DRONES ---
        if (drone.mode === 'LANDED') {
            drone.landedTick = (drone.landedTick || 0) + 1;
            if (drone.landedTick < 20) return Promise.resolve(); 
            drone.landedTick = 0; 
        }

        // --- 2. FLIGHT PHYSICS ---
        if (drone.mode === 'PATROL') {
            const target = drone.patrolRoute[drone.targetWaypointIndex]; 
            const dist = getDistance(drone.lat, drone.lng, target.lat, target.lng);

            if (dist < 0.001) {
                drone.targetWaypointIndex = (drone.targetWaypointIndex + 1) % drone.patrolRoute.length;
            }

            const stepSize = 0.00005 * (drone.speed / 50);
            drone.lat += ((target.lat - drone.lat) / dist) * stepSize;
            drone.lng += ((target.lng - drone.lng) / dist) * stepSize;
            
            drone.altitude = 150 + (Math.sin(Date.now() / 1000 + index) * 10);
            
        } else if (drone.mode === 'RTL') {
            const dist = getDistance(drone.lat, drone.lng, baseCoords.lat, baseCoords.lng);

            if (dist > 0.0005) { 
                const stepSize = 0.00008; 
                drone.lat += ((baseCoords.lat - drone.lat) / dist) * stepSize;
                drone.lng += ((baseCoords.lng - drone.lng) / dist) * stepSize;
            } else {
                if (drone.altitude > 1) {
                    drone.altitude *= 0.85; 
                } else {
                    drone.mode = 'LANDED';
                    drone.altitude = 0;
                    drone.speed = 0;
                    console.log(`\n✅ [DRONE ${drone.id.substring(0,6)}] TOUCHDOWN SECURED.`);
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
                
                // --- 4. THE COMMAND INTERCEPTOR ---
                if (data) {
                    if (data.command === 'RTL' && drone.mode !== 'LANDED') {
                        console.log(`\n🚨 [DRONE ${drone.id.substring(0,6)}] RTL OVERRIDE.`);
                        drone.mode = 'RTL';
                    } 
                    else if (data.command === 'UPDATE_ROUTE') {
                        console.log(`\n🗺️ [DRONE ${drone.id.substring(0,6)}] NEW WAYPOINTS RECEIVED. RECALCULATING VECTOR.`);
                        
                        // Overwrite the drone's brain with the new route
                        drone.patrolRoute = data.data;
                        drone.targetWaypointIndex = 0; // Instantly target the first new point
                        
                        // If the drone was landed or returning, reactivate the motors
                        if (drone.mode === 'LANDED' || drone.mode === 'RTL') {
                            drone.speed = 50 + (Math.random() * 30);
                            console.log(`\n🚀 [DRONE ${drone.id.substring(0,6)}] LAUNCHING FOR NEW PATROL.`);
                        }
                        drone.mode = 'PATROL';
                    }
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