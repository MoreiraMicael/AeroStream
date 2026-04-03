const crypto = require('crypto');

const API_URL = 'http://localhost:5233/telemetry';
const SWARM_SIZE = 10;
const FREQUENCY_MS = 100; // 10Hz (10 times a second per drone)

// Center point: Peniche, Portugal
const centerLat = 39.36;
const centerLng = -9.14;

// Generate unique Drone IDs and starting offsets
const drones = Array.from({ length: SWARM_SIZE }).map((_, i) => ({
    id: crypto.randomUUID(),
    offsetLat: (Math.random() - 0.5) * 0.05, // Spread them out
    offsetLng: (Math.random() - 0.5) * 0.05,
    speed: 50 + (Math.random() * 30), // Random speeds between 50-80
    step: Math.random() * 100 // Start at different points in the circle
}));

let successCount = 0;
let failCount = 0;

console.log(`🚁 Launching Swarm Simulator...`);
console.log(`🎯 Target: ${API_URL}`);
console.log(`🛸 Drones: ${SWARM_SIZE}`);
console.log(`⚡ Frequency: ${1000 / FREQUENCY_MS}Hz per drone (${(1000 / FREQUENCY_MS) * SWARM_SIZE} packets/sec total)\n`);

setInterval(async () => {
    const promises = drones.map(drone => {
        drone.step += 0.05;
        
        // Calculate orbit
        const currentLat = centerLat + drone.offsetLat + (Math.sin(drone.step) * 0.02);
        const currentLng = centerLng + drone.offsetLng + (Math.cos(drone.step) * 0.02);
        const currentAlt = 150 + (Math.sin(drone.step * 0.5) * 20);

        const payload = {
            deviceId: drone.id,
            latitude: currentLat,
            longitude: currentLng,
            altitude: currentAlt,
            speed: drone.speed,
            pitch: 0, roll: 0, yaw: 0,
            timestamp: new Date().toISOString()
        };

        return fetch(API_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(res => {
            if (res.ok) successCount++;
            else failCount++;
        }).catch(() => failCount++);
    });

    await Promise.all(promises);
}, FREQUENCY_MS);

// Print metrics every second
setInterval(() => {
    process.stdout.write(`\r📊 Throughput: ${successCount} pkt/sec | ❌ Dropped: ${failCount} | ⏱️ Queueing healthy...`);
    successCount = 0; 
    failCount = 0;
}, 1000);