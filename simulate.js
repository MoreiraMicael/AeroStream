const crypto = require('crypto');

const API_URL = 'http://localhost:5233/telemetry';
const SWARM_SIZE = 5;
const FREQUENCY_MS = 50;  // Doubled frequency: 20 Hz for smoother motion
const HUNTER_COUNT = 1;
const PATROL_COUNT = Math.max(0, SWARM_SIZE - HUNTER_COUNT);
const HUNTER_TAG_RADIUS = 0.00035;

// Battery configuration (in Volts)
const BATTERY_MAX = 23.5;           // Full charge (6S LiPo typical)
const BATTERY_CRITICAL = 18.0;      // Emergency RTL threshold
const BATTERY_ULTRA_CRITICAL = 16.5; // Force land threshold
const BATTERY_TAKEOFF_MIN = 21.0;    // Block launch if below this
const BATTERY_DRAIN_FLIGHT = 0.02;  // V/sec during flight
const BATTERY_DRAIN_LANDED = 0.001; // V/sec when landed (minimal)
const HUNTER_DRAIN_MULTIPLIER = 1.3; // Hunters drain faster due to aggressive maneuvering

const baseCoords = { lat: 39.586514, lng: -9.021444 };

const getDistance = (lat1, lng1, lat2, lng2) => {
    return Math.sqrt(Math.pow(lat2 - lat1, 2) + Math.pow(lng2 - lng1, 2));
};

const moveTowards = (drone, targetLat, targetLng, stepSize) => {
    const dist = getDistance(drone.lat, drone.lng, targetLat, targetLng);
    if (dist < 0.000001) return dist;

    drone.lat += ((targetLat - drone.lat) / dist) * stepSize;
    drone.lng += ((targetLng - drone.lng) / dist) * stepSize;
    return dist;
};

const shortId = (id) => id.substring(0, 6);

// Battery drain logic
const updateBattery = (drone) => {
    const drainRate = drone.mode === 'LANDED' 
        ? BATTERY_DRAIN_LANDED 
        : BATTERY_DRAIN_FLIGHT * (drone.type === 'HUNTER' ? HUNTER_DRAIN_MULTIPLIER : 1);
    
    drone.batteryVoltage -= drainRate * (FREQUENCY_MS / 1000); // Scale to 100ms intervals
    drone.batteryVoltage = Math.max(drone.batteryVoltage, 0); // Never go negative
};

// Initialize Swarm
const drones = Array.from({ length: SWARM_SIZE }).map((_, i) => {
    const type = i < PATROL_COUNT ? 'PATROL' : 'HUNTER';
    return {
        id: crypto.randomUUID(),
        type,
        lat: baseCoords.lat + (Math.random() - 0.5) * 0.0004,
        lng: baseCoords.lng + (Math.random() - 0.5) * 0.0004,
        patrolRoute: [],
        routeAssigned: false,
        targetWaypointIndex: 0,
        speed: 0,
        altitude: 0,
        mode: 'LANDED',
        hunterActive: false,
        landedTick: 0,
        taggedByHunter: false,
        droneType: type,
        batteryVoltage: BATTERY_MAX + (Math.random() * 0.2), // ±0.1V variance
        emergencyTriggered: false,
    };
});

let successCount = 0;
let failCount = 0;

const patrolDrones = drones.filter((drone) => drone.type === 'PATROL').length;
const hunterDrones = drones.filter((drone) => drone.type === 'HUNTER').length;

console.log('Launching Role-Based Swarm Simulator...');
console.log(`Target: ${API_URL}`);
console.log(`Total drones: ${SWARM_SIZE} | Patrol: ${patrolDrones} | Hunter: ${hunterDrones}`);
console.log('All drones start LANDED. Patrol drones require UPDATE_ROUTE to launch.');
console.log('Hunter requires UPDATE_ROUTE and HUNTER_ON command to begin chase mode.');

setInterval(async () => {
    const promises = drones.map((drone, index) => {
        
        // --- 0. BATTERY DRAIN ---
        updateBattery(drone);

        // --- 0.5. EMERGENCY BATTERY CHECK ---
        if (drone.batteryVoltage <= BATTERY_ULTRA_CRITICAL && drone.mode !== 'LANDED') {
            // Ultra-critical: force immediate land in place
            if (!drone.emergencyTriggered) {
                console.log(`\n[EMERGENCY] Drone ${shortId(drone.id)} ULTRA-CRITICAL (${drone.batteryVoltage.toFixed(1)}V). Force landing immediately.`);
                drone.emergencyTriggered = true;
            }
            drone.altitude *= 0.95; // Rapid descent
            if (drone.altitude <= 0.5) {
                drone.mode = 'LANDED';
                drone.altitude = 0;
                drone.speed = 0;
            }
            // Continue to telemetry reporting below
        } else if (drone.batteryVoltage <= BATTERY_CRITICAL && drone.mode !== 'LANDED' && drone.mode !== 'RTL') {
            // Critical: trigger emergency RTL
            if (!drone.emergencyTriggered) {
                console.log(`\n[EMERGENCY] Drone ${shortId(drone.id)} CRITICAL (${drone.batteryVoltage.toFixed(1)}V). Emergency RTL initiated.`);
                drone.emergencyTriggered = true;
            }
            drone.mode = 'RTL';
        } else if (drone.batteryVoltage > BATTERY_CRITICAL + 1.0) {
            // Reset emergency flag once above critical + hysteresis
            drone.emergencyTriggered = false;
        }
        
        // --- 1. THROTTLE LOGIC FOR LANDED DRONES ---
        if (drone.mode === 'LANDED') {
            drone.landedTick = (drone.landedTick || 0) + 1;
            if (drone.landedTick < 20) return Promise.resolve(); 
            drone.landedTick = 0; 
        }

        // --- 2. FLIGHT PHYSICS ---
        if (drone.mode === 'PATROL') {
            if (!drone.routeAssigned || drone.patrolRoute.length === 0) {
                drone.mode = 'LANDED';
                drone.altitude = 0;
                drone.speed = 0;
            }

            const target = drone.patrolRoute[drone.targetWaypointIndex]; 
            const dist = target
                ? getDistance(drone.lat, drone.lng, target.lat, target.lng)
                : 0;

            if (dist < 0.001) {
                drone.targetWaypointIndex = (drone.targetWaypointIndex + 1) % drone.patrolRoute.length;
            }

            const stepSize = 0.00005 * (drone.speed / 50);
            if (target && dist > 0.000001) {
                moveTowards(drone, target.lat, target.lng, stepSize);
            }
            
            drone.altitude = 150 + (Math.sin(Date.now() / 1000 + index) * 10);
            
        } else if (drone.mode === 'HUNT') {
            const patrolTargets = drones.filter((candidate) => candidate.type === 'PATROL' && candidate.mode === 'PATROL');
            if (patrolTargets.length === 0) {
                drone.altitude = 120 + (Math.sin(Date.now() / 1000 + index) * 5);
            } else {
                let nearest = patrolTargets[0];
                let nearestDist = getDistance(drone.lat, drone.lng, nearest.lat, nearest.lng);

                for (const candidate of patrolTargets.slice(1)) {
                    const candidateDist = getDistance(drone.lat, drone.lng, candidate.lat, candidate.lng);
                    if (candidateDist < nearestDist) {
                        nearest = candidate;
                        nearestDist = candidateDist;
                    }
                }

                moveTowards(drone, nearest.lat, nearest.lng, 0.00009);
                drone.altitude = 135 + (Math.sin(Date.now() / 900 + index) * 8);
                drone.speed = 90;

                if (nearestDist <= HUNTER_TAG_RADIUS && nearest.mode !== 'RTL' && nearest.mode !== 'LANDED') {
                    nearest.mode = 'RTL';
                    nearest.taggedByHunter = true;
                    console.log(`\n[TAG] Hunter ${shortId(drone.id)} tagged patrol ${shortId(nearest.id)}. Forcing RTL.`);
                }
            }
        } else if (drone.mode === 'RTL') {
            drone.speed = 45;
            const dist = getDistance(drone.lat, drone.lng, baseCoords.lat, baseCoords.lng);

            if (dist > 0.0005) { 
                const stepSize = 0.00008; 
                moveTowards(drone, baseCoords.lat, baseCoords.lng, stepSize);
            } else {
                if (drone.altitude > 1) {
                    drone.altitude *= 0.85; 
                } else {
                    drone.mode = 'LANDED';
                    drone.altitude = 0;
                    drone.speed = 0;
                    if (drone.type === 'PATROL' && drone.taggedByHunter) {
                        console.log(`\n[BASE] Patrol ${shortId(drone.id)} returned to base after hunter tag.`);
                        drone.taggedByHunter = false;
                    } else {
                        console.log(`\n[BASE] Drone ${shortId(drone.id)} touchdown secured.`);
                    }
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
            batteryVoltage: drone.batteryVoltage,
            droneType: drone.type,
            timestamp: new Date().toISOString()
        };

        return fetch(API_URL, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Drone-Id': drone.id,
            },
            body: JSON.stringify(payload)
        }).then(async res => {
            if (res.ok) {
                successCount++;
                const data = await res.json().catch(() => null);
                
                // --- 4. THE COMMAND INTERCEPTOR ---
                if (data) {
                    if (data.command === 'RTL' && drone.mode !== 'LANDED') {
                        console.log(`\n[CMD] Drone ${shortId(drone.id)} RTL override.`);
                        drone.mode = 'RTL';
                    } 
                    else if (data.command === 'UPDATE_ROUTE') {
                        // Block takeoff if battery too low
                        if (drone.batteryVoltage < BATTERY_TAKEOFF_MIN) {
                            console.log(`\n[CMD-REJECTED] Drone ${shortId(drone.id)} route rejected: battery too low (${drone.batteryVoltage.toFixed(1)}V < ${BATTERY_TAKEOFF_MIN}V).`);
                            return;
                        }

                        if (Array.isArray(data.data) && data.data.length > 0) {
                            drone.patrolRoute = data.data;
                            drone.routeAssigned = true;
                            drone.targetWaypointIndex = 0;
                            console.log(`\n[CMD] Drone ${shortId(drone.id)} route update received (${data.data.length} WPs).`);

                            if (drone.type === 'PATROL') {
                                drone.mode = 'PATROL';
                                drone.speed = 50 + (Math.random() * 30);
                                drone.altitude = Math.max(drone.altitude, 120);
                                console.log(`[TAKEOFF] Patrol ${shortId(drone.id)} launching for patrol route.`);
                            } else if (drone.type === 'HUNTER') {
                                console.log(`[READY] Hunter ${shortId(drone.id)} route loaded. Awaiting HUNTER_ON.`);
                            }
                        }
                    }
                    else if (data.command === 'HUNTER_ON' && drone.type === 'HUNTER') {
                        if (!drone.routeAssigned) {
                            console.log(`\n[HUNTER] Hunter ${shortId(drone.id)} cannot activate: assign route first.`);
                        } else {
                            drone.hunterActive = true;
                            drone.mode = 'HUNT';
                            drone.speed = 90;
                            drone.altitude = Math.max(drone.altitude, 130);
                            console.log(`\n[HUNTER] Hunter ${shortId(drone.id)} activated. Beginning pursuit.`);
                        }
                    }
                    else if (data.command === 'HUNTER_OFF' && drone.type === 'HUNTER') {
                        drone.hunterActive = false;
                        drone.mode = 'RTL';
                        console.log(`\n[HUNTER] Hunter ${shortId(drone.id)} deactivated. Returning to base.`);
                    }
                }
            } else failCount++;
        }).catch(() => failCount++);
    });

    await Promise.all(promises);
}, FREQUENCY_MS);

setInterval(() => {
    const airbornePatrol = drones.filter((drone) => drone.type === 'PATROL' && drone.mode === 'PATROL').length;
    const returningPatrol = drones.filter((drone) => drone.type === 'PATROL' && drone.mode === 'RTL').length;
    const landedPatrol = drones.filter((drone) => drone.type === 'PATROL' && drone.mode === 'LANDED').length;
    const hunterMode = drones.find((drone) => drone.type === 'HUNTER')?.mode ?? 'N/A';

    process.stdout.write(`\rThroughput: ${successCount} pkt/sec | Dropped: ${failCount} | Patrol[P:${airbornePatrol} RTL:${returningPatrol} L:${landedPatrol}] | Hunter:${hunterMode}`);
    successCount = 0; 
    failCount = 0;
}, 1000);