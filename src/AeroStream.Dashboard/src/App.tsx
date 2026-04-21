import { useEffect, useState, useRef, type MutableRefObject } from 'react';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { MapContainer, TileLayer, Marker, Popup, Polyline, Polygon, useMapEvents } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';
import L from 'leaflet';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5233';
const NOTIFICATION_TTL_MS = 4500;
const EXPIRED_DRONE_MS = 10000;

// Tactical Icons
const TacticalIcon = L.icon({
    iconUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-icon.png',
    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34]
});

  const HunterIcon = L.divIcon({
    className: 'hunter-marker',
    html: '<div style="width: 22px; height: 22px; border-radius: 50%; background: radial-gradient(circle at 30% 30%, #fb7185, #be123c); border: 2px solid #fff; box-shadow: 0 0 0 3px rgba(190, 24, 93, 0.28), 0 0 14px rgba(190, 24, 93, 0.5);"></div>',
    iconSize: [22, 22],
    iconAnchor: [11, 11],
    popupAnchor: [0, -12]
  });

// A small red dot for drawing waypoints
const WaypointIcon = L.divIcon({
    className: 'custom-waypoint',
    html: '<div style="background-color: #ef4444; width: 12px; height: 12px; border-radius: 50%; border: 2px solid white; box-shadow: 0 0 4px rgba(0,0,0,0.5);"></div>',
    iconSize: [16, 16],
    iconAnchor: [8, 8]
});

interface Telemetry {
  deviceId: string;
  latitude: number;
  longitude: number;
  altitude: number;
  speed: number;
  pitch: number;
  roll: number;
  yaw: number;
  batteryVoltage?: number;
  droneType?: string;
  timestamp: string;
}

interface Notification {
  id: number;
  tone: 'info' | 'success' | 'error';
  message: string;
}

type DroneType = 'PATROL' | 'HUNTER' | 'UNKNOWN';

function getDroneType(drone: Telemetry): DroneType {
  if (drone.droneType === 'PATROL') return 'PATROL';
  if (drone.droneType === 'HUNTER') return 'HUNTER';
  if (drone.deviceId.startsWith('PATROL-')) return 'PATROL';
  if (drone.deviceId.startsWith('HUNTER-')) return 'HUNTER';
  return 'UNKNOWN';
}

function getDroneShortId(deviceId: string) {
  const parts = deviceId.split('-');
  if (parts.length > 1) {
    return parts[1].slice(0, 6);
  }
  return deviceId.slice(0, 6);
}

function isHunter(drone: Telemetry) {
  return getDroneType(drone) === 'HUNTER';
}

async function requestJson(path: string, init: RequestInit) {
  const response = await fetch(`${API_BASE_URL}${path}`, init);
  const contentType = response.headers.get('content-type') ?? '';
  const isJsonResponse = contentType.includes('application/json');
  const responseBody = isJsonResponse ? await response.json() : await response.text();

  if (!response.ok) {
    const detail = typeof responseBody === 'string'
      ? responseBody
      : responseBody?.message ?? `HTTP ${response.status}`;
    throw new Error(detail || `HTTP ${response.status}`);
  }

  return responseBody;
}

// NEW: Component to handle map clicks for drawing
function MapClickHandler({ isDrawing, isDrawingGeofence, onMapClick }: { isDrawing: boolean, isDrawingGeofence: boolean, onMapClick: (latlng: [number, number]) => void }) {
    useMapEvents({
        click(e) {
            if (isDrawing || isDrawingGeofence) {
                onMapClick([e.latlng.lat, e.latlng.lng]);
            }
        }
    });
    return null; // This component doesn't render any DOM elements itself
}

function AnimatedDroneMarkers({
  dronesRef,
  prevDronesRef,
  telemetryTimestampRef,
}: {
  dronesRef: MutableRefObject<Record<string, Telemetry>>;
  prevDronesRef: MutableRefObject<Record<string, Telemetry>>;
  telemetryTimestampRef: MutableRefObject<number>;
}) {
  const [animatedDrones, setAnimatedDrones] = useState<Record<string, Telemetry>>({});

  useEffect(() => {
    let animFrameId = 0;

    const renderFrame = () => {
      const now = Date.now();
      const timeSinceLastTelemetry = now - telemetryTimestampRef.current;
      const interpolationAlpha = Math.min(timeSinceLastTelemetry / 50, 1);
      const interpolatedDrones: Record<string, Telemetry> = {};

      for (const deviceId in dronesRef.current) {
        const current = dronesRef.current[deviceId];
        const prev = prevDronesRef.current[deviceId];

        if (prev && prev.deviceId === current.deviceId) {
          interpolatedDrones[deviceId] = {
            ...current,
            latitude: prev.latitude + (current.latitude - prev.latitude) * interpolationAlpha,
            longitude: prev.longitude + (current.longitude - prev.longitude) * interpolationAlpha,
            altitude: prev.altitude + (current.altitude - prev.altitude) * interpolationAlpha,
            speed: prev.speed + (current.speed - prev.speed) * interpolationAlpha,
          };
        } else {
          interpolatedDrones[deviceId] = current;
        }
      }

      setAnimatedDrones(interpolatedDrones);
      animFrameId = requestAnimationFrame(renderFrame);
    };

    animFrameId = requestAnimationFrame(renderFrame);

    return () => {
      cancelAnimationFrame(animFrameId);
    };
  }, [dronesRef, prevDronesRef, telemetryTimestampRef]);

  return (
    <>
      {Object.entries(animatedDrones).map(([id, data]) => (
        <Marker key={id} position={[data.latitude, data.longitude]} icon={isHunter(data) ? HunterIcon : TacticalIcon}>
          <Popup>
            DRONE_ID: {id}
            <br />
            TYPE: {getDroneType(data)}
          </Popup>
        </Marker>
      ))}
    </>
  );
}


function App() {
  const dronesRef   = useRef<Record<string, Telemetry>>({});
  const pathsRef    = useRef<Record<string, [number, number][]>>({});
  const lastSeenRef = useRef<Record<string, number>>({});
  const prevDronesRef = useRef<Record<string, Telemetry>>({});
  const telemetryTimestampRef = useRef(Date.now());
  const notificationIdRef = useRef(0);

  const [drones, setDrones] = useState<Record<string, Telemetry>>({});
  const [paths, setPaths]   = useState<Record<string, [number, number][]>>({});
  const [lastSeen, setLastSeen] = useState<Record<string, number>>({});
  const [status, setStatus] = useState("DISCONNECTED");
  const [notifications, setNotifications] = useState<Notification[]>([]);

  // NEW: State for the dynamic route planner
  const [isDrawMode, setIsDrawMode] = useState(false);
  const [newRoute, setNewRoute] = useState<[number, number][]>([]);

  // PHASE 3: State for geofence drawing
  const [isDrawingGeofence, setIsDrawingGeofence] = useState(false);
  const [geofencePolygon, setGeofencePolygon] = useState<[number, number][]>([]);

  const pushNotification = (tone: Notification['tone'], message: string) => {
    const id = notificationIdRef.current++;
    setNotifications(current => [...current, { id, tone, message }]);

    window.setTimeout(() => {
      setNotifications(current => current.filter(notification => notification.id !== id));
    }, NOTIFICATION_TTL_MS);
  };

  // Existing RTL Command
  const issueRTL = async (deviceId: string) => {
    try {
      await requestJson(`/command/${deviceId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: 'RTL' })
      });
      pushNotification('success', `RTL queued for drone ${getDroneShortId(deviceId)}.`);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown command failure';
      pushNotification('error', `RTL failed for drone ${getDroneShortId(deviceId)}: ${message}`);
      console.error("C2 Link Failed", err);
    }
  };

  const issueHunterCommand = async (deviceId: string, enabled: boolean) => {
    const command = enabled ? 'HUNTER_ON' : 'HUNTER_OFF';
    try {
      await requestJson(`/command/${deviceId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command })
      });

      pushNotification('success', `Hunter ${getDroneShortId(deviceId)} ${enabled ? 'activated' : 'deactivated'}.`);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown hunter command failure';
      pushNotification('error', `${command} failed for hunter ${getDroneShortId(deviceId)}: ${message}`);
      console.error("Hunter command failed", err);
    }
  };

  const issueSwarmRTL = async () => {
    const activeIds = Object.keys(dronesRef.current);
    if (activeIds.length === 0) {
      pushNotification('error', 'No active drones available for swarm RTL.');
      return;
    }

    const results = await Promise.allSettled(
      activeIds.map(id =>
        requestJson(`/command/${id}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ command: 'RTL' }),
        }),
      ),
    );

    const successCount = results.filter(result => result.status === 'fulfilled').length;
    const failedCount = results.length - successCount;

    if (failedCount === 0) {
      pushNotification('success', `Swarm RTL queued for ${successCount} drone(s).`);
      return;
    }

    const firstFailure = results.find(result => result.status === 'rejected');
    const failureMessage = firstFailure && firstFailure.status === 'rejected' && firstFailure.reason instanceof Error
      ? firstFailure.reason.message
      : 'Unknown error';

    pushNotification('error', `Swarm RTL queued for ${successCount}/${activeIds.length} drone(s). First failure: ${failureMessage}`);
  };

  const wipeDatabase = async () => {
    const confirmed = window.confirm('Wipe persisted telemetry and clear queued commands/geofence state? This cannot be undone.');
    if (!confirmed) {
      return;
    }

    try {
      const result = await requestJson('/admin/reset', {
        method: 'POST',
      }) as { deletedTelemetry?: number };

      dronesRef.current = {};
      pathsRef.current = {};
      lastSeenRef.current = {};
      prevDronesRef.current = {};
      telemetryTimestampRef.current = Date.now();
      setDrones({});
      setPaths({});
      setLastSeen({});
      setGeofencePolygon([]);
      setIsDrawingGeofence(false);
      setNewRoute([]);
      setIsDrawMode(false);

      const deletedTelemetry = typeof result?.deletedTelemetry === 'number' ? result.deletedTelemetry : 0;
      pushNotification('success', `Database reset complete. Deleted ${deletedTelemetry} telemetry row(s).`);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown reset failure';
      pushNotification('error', `Database reset failed: ${message}`);
      console.error('Database reset failed', err);
    }
  };

  // NEW: Deploy the drawn route to the entire swarm
  const deploySwarmRoute = async () => {
      if (newRoute.length === 0) {
        pushNotification('info', 'Add at least one waypoint before deploying a route.');
        return;
      }

      // Grab all active drone IDs from our ref
      const activeIds = Object.keys(dronesRef.current);
      if (activeIds.length === 0) {
        pushNotification('error', 'No active drones available for route deployment.');
        return;
      }
      
      // Format the payload to match our new C# API model
      const payload = {
          deviceIds: activeIds,
          route: newRoute.map(coord => ({ lat: coord[0], lng: coord[1] }))
      };

      try {
          await requestJson('/command/swarm/route', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload)
          });
          pushNotification('success', `Route with ${newRoute.length} waypoint(s) queued for ${activeIds.length} drone(s).`);
          
          // Reset UI
          setIsDrawMode(false);
          setNewRoute([]);
      } catch (err) {
          const message = err instanceof Error ? err.message : 'Unknown route deployment failure';
          pushNotification('error', `Route deployment failed: ${message}`);
          console.error("Route Deployment Failed", err);
      }
  };

  // PHASE 3: Deploy geofence to the backend
  const deployGeofence = async () => {
      if (geofencePolygon.length < 3) {
        pushNotification('error', 'Geofence requires at least 3 vertices.');
          console.error("Geofence requires at least 3 vertices");
          return;
      }

      const payload = {
          coordinates: geofencePolygon.map(coord => ({ lat: coord[0], lng: coord[1] }))
      };

      try {
          const result = await requestJson('/command/swarm/geofence', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload)
          });
          const vertexCount = typeof result === 'object' && result !== null && 'vertexCount' in result
          ? String(result.vertexCount)
          : String(geofencePolygon.length);
          pushNotification('success', `Geofence deployed with ${vertexCount} vertices.`);
          
          // Reset UI
          setIsDrawingGeofence(false);
          setGeofencePolygon([]);
      } catch (err) {
          const message = err instanceof Error ? err.message : 'Unknown geofence deployment failure';
          pushNotification('error', `Geofence deployment failed: ${message}`);
          console.error("Geofence Deployment Failed", err);
      }
  };

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/telemetryHub`)
      .withAutomaticReconnect()
      .build();

    connection.onreconnecting(() => {
      setStatus("RECONNECTING");
      pushNotification('info', 'Telemetry link reconnecting.');
    });

    connection.onreconnected(() => {
      setStatus("LINK_OK");
      pushNotification('success', 'Telemetry link restored.');
    });

    connection.onclose(() => {
      setStatus("LINK_LOST");
      pushNotification('error', 'Telemetry link lost.');
    });

    connection.start()
      .then(() => {
        setStatus("LINK_OK");
        pushNotification('success', 'Telemetry link established.');
        connection.on("ReceiveTelemetry", (data: Telemetry) => {
          const previousTelemetry = dronesRef.current[data.deviceId];
          if (previousTelemetry) {
            prevDronesRef.current[data.deviceId] = previousTelemetry;
          }
          dronesRef.current[data.deviceId]   = data;
          lastSeenRef.current[data.deviceId] = Date.now();
          telemetryTimestampRef.current = Date.now();

          if (!pathsRef.current[data.deviceId]) {
            pathsRef.current[data.deviceId] = [];
          }
          
          pathsRef.current[data.deviceId].push([data.latitude, data.longitude]);
          if (pathsRef.current[data.deviceId].length > 50) {
             pathsRef.current[data.deviceId].shift();
          }
        });
      })
      .catch(() => setStatus("LINK_LOST"));

    const snapshotInterval = setInterval(() => {
      const now = Date.now();

      // Drop drones from previous sessions after they have been stale long enough.
      for (const deviceId of Object.keys(lastSeenRef.current)) {
        if ((now - lastSeenRef.current[deviceId]) <= EXPIRED_DRONE_MS) {
          continue;
        }

        delete dronesRef.current[deviceId];
        delete prevDronesRef.current[deviceId];
        delete pathsRef.current[deviceId];
        delete lastSeenRef.current[deviceId];
      }

      setDrones({ ...dronesRef.current });
      setPaths({ ...pathsRef.current });
      setLastSeen({ ...lastSeenRef.current });
    }, 100);

    return () => { 
        connection.stop();
        clearInterval(snapshotInterval);
    };
  }, []);

  const staleDroneCount = Object.values(drones).filter(
    drone => (Date.now() - (lastSeen[drone.deviceId] ?? 0)) > 2000,
  ).length;
  const hunterCount = Object.values(drones).filter(drone => getDroneType(drone) === 'HUNTER').length;
  const patrolCount = Object.values(drones).filter(drone => getDroneType(drone) === 'PATROL').length;

  const notificationToneStyles: Record<Notification['tone'], { background: string; borderColor: string; color: string }> = {
    info: { background: '#dbeafe', borderColor: '#60a5fa', color: '#1d4ed8' },
    success: { background: '#dcfce7', borderColor: '#4ade80', color: '#166534' },
    error: { background: '#fee2e2', borderColor: '#f87171', color: '#b91c1c' },
  };

  return (
    <div style={{ height: "100vh", width: "100vw", background: '#f1f5f9', color: '#0f172a', fontFamily: 'monospace' }}>
      <div style={{ position: 'absolute', top: 72, right: 20, zIndex: 1100, display: 'flex', flexDirection: 'column', gap: '10px', width: 320, pointerEvents: 'none' }}>
        {notifications.map(notification => {
          const toneStyle = notificationToneStyles[notification.tone];
          return (
            <div
              key={notification.id}
              style={{
                background: toneStyle.background,
                color: toneStyle.color,
                border: `1px solid ${toneStyle.borderColor}`,
                borderLeft: `4px solid ${toneStyle.borderColor}`,
                borderRadius: '8px',
                padding: '12px 14px',
                boxShadow: '0 10px 20px rgba(15, 23, 42, 0.12)',
                fontSize: '0.78rem',
                textAlign: 'left',
              }}>
              {notification.message}
            </div>
          );
        })}
      </div>
      
      <header style={{ position: 'absolute', top: 0, width: '100%', zIndex: 1000, background: '#ffffff', borderBottom: '2px solid #3b82f6', padding: '12px 20px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', boxShadow: '0 2px 4px rgba(0,0,0,0.1)' }}>
        <div style={{ fontWeight: 'bold' }}>
          <span style={{ color: '#3b82f6' }}>AEROSTREAM</span> // SWARM_COMMAND
        </div>

        {/* NEW: Tactical Mission Planner Toolbar */}
        <div style={{ display: 'flex', gap: '10px' }}>
            {isDrawMode ? (
                <>
                    <button onClick={() => setNewRoute([])} style={{ background: '#64748b', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        Clear Route
                    </button>
                    <button onClick={() => setIsDrawMode(false)} style={{ background: '#f87171', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        Cancel
                    </button>
                    <button onClick={deploySwarmRoute} style={{ background: '#16a34a', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        Deploy Swarm Route ({newRoute.length} WPs)
                    </button>
                </>
            ) : isDrawingGeofence ? (
                <>
                    <button onClick={() => setGeofencePolygon([])} style={{ background: '#64748b', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        Clear Geofence
                    </button>
                    <button onClick={() => setIsDrawingGeofence(false)} style={{ background: '#f87171', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        Cancel
                    </button>
                    <button onClick={deployGeofence} disabled={geofencePolygon.length < 3} style={{ background: geofencePolygon.length < 3 ? '#9ca3af' : '#16a34a', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: geofencePolygon.length < 3 ? 'not-allowed' : 'pointer', fontWeight: 'bold' }}>
                        Deploy Geofence ({geofencePolygon.length} vertices)
                    </button>
                </>
            ) : (
                <>
                    <button onClick={() => setIsDrawMode(true)} style={{ background: '#3b82f6', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        + Draw Patrol Route
                    </button>
                    <button onClick={() => setIsDrawingGeofence(true)} style={{ background: '#8b5cf6', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                        🛡️ Draw Geofence
                    </button>
                  <button onClick={issueSwarmRTL} style={{ background: '#b91c1c', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                    RTL ALL ({Object.keys(drones).length})
                  </button>
                  <button onClick={wipeDatabase} style={{ background: '#111827', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                    WIPE DB
                  </button>
                </>
            )}
        </div>

        <div style={{ fontWeight: 'bold', color: status === "LINK_OK" ? "#16a34a" : "#dc2626" }}>
          SIGNAL: {status} | DRONES: {Object.keys(drones).length}
        </div>
      </header>

      {staleDroneCount > 0 && (
        <div style={{ position: 'absolute', top: 78, left: '50%', transform: 'translateX(-50%)', zIndex: 1000, background: '#fff7ed', color: '#9a3412', border: '1px solid #fdba74', borderRadius: '999px', padding: '8px 14px', fontSize: '0.72rem', fontWeight: 'bold', boxShadow: '0 8px 16px rgba(154, 52, 18, 0.12)' }}>
          ALERT: {staleDroneCount} drone{staleDroneCount === 1 ? '' : 's'} missing fresh telemetry.
        </div>
      )}

      {/* Swarm Telemetry Sidebar */}
      <aside style={{ position: 'absolute', left: 20, top: 80, width: 240, zIndex: 1000, background: '#ffffff', border: '1px solid #e2e8f0', padding: '20px', borderRadius: '8px', boxShadow: '0 10px 15px -3px rgba(0,0,0,0.1)', maxHeight: '80vh', overflowY: 'auto' }}>
        <h4 style={{ margin: '0 0 15px 0', fontSize: '0.75rem', color: '#64748b' }}>SWARM_TELEMETRY</h4>
        <div style={{ marginBottom: '10px', fontSize: '0.66rem', color: '#475569' }}>
          PATROL: <strong style={{ color: '#2563eb' }}>{patrolCount}</strong> | HUNTER: <strong style={{ color: '#b91c1c' }}>{hunterCount}</strong>
        </div>
        {Object.values(drones).length === 0 && <span style={{ color: '#94a3b8' }}>AWAITING_SWARM...</span>}
        
        {Object.values(drones).map(drone => {
          const droneType = getDroneType(drone);
          const hunter = isHunter(drone);
          const stale    = (Date.now() - (lastSeen[drone.deviceId] ?? 0)) > 2000;
          const batColor = !drone.batteryVoltage || drone.batteryVoltage === 0
            ? '#94a3b8'
            : drone.batteryVoltage < 20.0 ? '#dc2626'
            : drone.batteryVoltage < 21.6 ? '#f59e0b'
            : '#16a34a';
          const typeBadgeStyle = droneType === 'HUNTER'
            ? { background: '#fee2e2', color: '#b91c1c', borderColor: '#fca5a5' }
            : droneType === 'PATROL'
              ? { background: '#dbeafe', color: '#1d4ed8', borderColor: '#93c5fd' }
              : { background: '#e2e8f0', color: '#475569', borderColor: '#cbd5e1' };

          return (
            <div key={drone.deviceId} style={{ fontSize: '0.8rem', borderBottom: hunter ? '1px solid #fecdd3' : '1px solid #f1f5f9', borderLeft: hunter ? '4px solid #be123c' : '4px solid transparent', background: hunter ? '#fff1f2' : 'transparent', borderRadius: '6px', padding: hunter ? '8px 8px 10px 10px' : '0 0 10px 0', marginBottom: '10px', opacity: stale ? 0.45 : 1 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '4px' }}>
                <span>
                  ID: <strong style={{ color: stale ? '#94a3b8' : '#3b82f6' }}>{getDroneShortId(drone.deviceId)}</strong>
                  {stale && <span style={{ color: '#f59e0b', marginLeft: '6px', fontSize: '0.65rem' }}>STALE</span>}
                </span>
                <button
                  onClick={() => issueRTL(drone.deviceId)}
                  style={{ background: hunter ? '#7f1d1d' : '#dc2626', color: 'white', border: 'none', padding: '3px 7px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold', fontSize: '0.65rem' }}>
                  RTL
                </button>
              </div>
              <div style={{ marginBottom: '4px' }}>
                <span style={{ background: typeBadgeStyle.background, color: typeBadgeStyle.color, border: `1px solid ${typeBadgeStyle.borderColor}`, borderRadius: '999px', padding: '1px 8px', fontSize: '0.62rem', fontWeight: 'bold' }}>
                  {droneType}
                </span>
                {hunter && (
                  <span style={{ marginLeft: '6px', background: '#ffe4e6', color: '#9f1239', border: '1px solid #fda4af', borderRadius: '999px', padding: '1px 8px', fontSize: '0.62rem', fontWeight: 'bold' }}>
                    TAG MODE
                  </span>
                )}
              </div>
              {droneType === 'HUNTER' && (
                <div style={{ display: 'flex', gap: '6px', marginBottom: '6px' }}>
                  <button
                    onClick={() => issueHunterCommand(drone.deviceId, true)}
                    style={{ background: 'linear-gradient(135deg, #be123c, #9f1239)', color: 'white', border: 'none', padding: '4px 8px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold', fontSize: '0.62rem', letterSpacing: '0.02em' }}>
                    HUNTER ON
                  </button>
                  <button
                    onClick={() => issueHunterCommand(drone.deviceId, false)}
                    style={{ background: '#475569', color: 'white', border: 'none', padding: '4px 8px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold', fontSize: '0.62rem', letterSpacing: '0.02em' }}>
                    HUNTER OFF
                  </button>
                </div>
              )}
              <div style={{ color: '#475569' }}>
                ALT <strong>{drone.altitude.toFixed(1)}</strong>m &nbsp;
                SPD <strong>{drone.speed.toFixed(0)}</strong>km/h
              </div>
              <div style={{ color: '#475569' }}>
                P <strong>{(drone.pitch ?? 0).toFixed(1)}</strong>°&nbsp;
                R <strong>{(drone.roll  ?? 0).toFixed(1)}</strong>°&nbsp;
                Y <strong>{(drone.yaw   ?? 0).toFixed(0)}</strong>°
              </div>
              {drone.batteryVoltage !== undefined && drone.batteryVoltage > 0 && (
                <div style={{ color: batColor, fontWeight: 'bold' }}>
                  BAT {drone.batteryVoltage.toFixed(1)}V
                </div>
              )}
            </div>
          );
        })}
      </aside>

      <MapContainer center={[39.586514, -9.021444]} zoom={14} zoomControl={false} style={{ height: "100%", width: "100%", cursor: isDrawMode || isDrawingGeofence ? 'crosshair' : 'grab' }}>
        <TileLayer url="https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png" />
        
        {/* NEW: Map Click Listener */}
        <MapClickHandler 
            isDrawing={isDrawMode} 
            isDrawingGeofence={isDrawingGeofence}
            onMapClick={(coord) => {
                if (isDrawMode) {
                    setNewRoute([...newRoute, coord]);
                } else if (isDrawingGeofence) {
                    setGeofencePolygon([...geofencePolygon, coord]);
                }
            }} 
        />

        {/* NEW: Draw the planned route preview */}
        {newRoute.length > 0 && (
            <Polyline positions={newRoute} pathOptions={{ color: '#ef4444', weight: 4, dashArray: '10, 10' }} />
        )}
        {newRoute.map((coord, idx) => (
            <Marker key={`wp-${idx}`} position={coord} icon={WaypointIcon} />
        ))}

        {/* PHASE 3: Draw the geofence boundary */}
        {geofencePolygon.length >= 3 && (
            <Polygon 
                positions={geofencePolygon} 
                pathOptions={{ color: '#ef4444', weight: 3, fillColor: '#ef4444', fillOpacity: 0.2 }} 
            />
        )}
        {geofencePolygon.map((coord, idx) => (
            <Marker key={`geo-${idx}`} position={coord} icon={WaypointIcon} />
        ))}

        {/* Existing: Draw Drone Trails */}
        {Object.entries(paths).map(([id, path]) => (
            <Polyline key={id} positions={path} pathOptions={{ color: '#3b82f6', weight: 3, opacity: 0.4 }} />
        ))}

        {/* Existing: Draw Drones */}
        <AnimatedDroneMarkers
          dronesRef={dronesRef}
          prevDronesRef={prevDronesRef}
          telemetryTimestampRef={telemetryTimestampRef}
        />
      </MapContainer>
    </div>
  );
}

export default App;