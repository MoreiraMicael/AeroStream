import { useEffect, useState, useRef } from 'react';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { MapContainer, TileLayer, Marker, Popup, Polyline, useMapEvents } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';
import L from 'leaflet';

// Tactical Icons
const TacticalIcon = L.icon({
    iconUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-icon.png',
    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34]
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
  timestamp: string;
}

// NEW: Component to handle map clicks for drawing
function MapClickHandler({ isDrawing, onMapClick }: { isDrawing: boolean, onMapClick: (latlng: [number, number]) => void }) {
    useMapEvents({
        click(e) {
            if (isDrawing) {
                onMapClick([e.latlng.lat, e.latlng.lng]);
            }
        }
    });
    return null; // This component doesn't render any DOM elements itself
}


function App() {
  const dronesRef   = useRef<Record<string, Telemetry>>({});
  const pathsRef    = useRef<Record<string, [number, number][]>>({});
  const lastSeenRef = useRef<Record<string, number>>({});

  const [drones, setDrones] = useState<Record<string, Telemetry>>({});
  const [paths, setPaths]   = useState<Record<string, [number, number][]>>({});
  const [lastSeen, setLastSeen] = useState<Record<string, number>>({});
  const [status, setStatus] = useState("DISCONNECTED");

  // NEW: State for the dynamic route planner
  const [isDrawMode, setIsDrawMode] = useState(false);
  const [newRoute, setNewRoute] = useState<[number, number][]>([]);

  // Existing RTL Command
  const issueRTL = async (deviceId: string) => {
    try {
      await fetch(`http://localhost:5233/command/${deviceId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: 'RTL' })
      });
      console.log(`Commanded ${deviceId} to Return to Launch.`);
    } catch (err) {
      console.error("C2 Link Failed", err);
    }
  };

  // NEW: Deploy the drawn route to the entire swarm
  const deploySwarmRoute = async () => {
      if (newRoute.length === 0) return;

      // Grab all active drone IDs from our ref
      const activeIds = Object.keys(dronesRef.current);
      
      // Format the payload to match our new C# API model
      const payload = {
          deviceIds: activeIds,
          route: newRoute.map(coord => ({ lat: coord[0], lng: coord[1] }))
      };

      try {
          await fetch(`http://localhost:5233/command/swarm/route`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload)
          });
          console.log(`Deployed new route of ${newRoute.length} waypoints to ${activeIds.length} drones.`);
          
          // Reset UI
          setIsDrawMode(false);
          setNewRoute([]);
      } catch (err) {
          console.error("Route Deployment Failed", err);
      }
  };

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("http://localhost:5233/telemetryHub")
      .withAutomaticReconnect()
      .build();

    connection.start()
      .then(() => {
        setStatus("LINK_OK");
        connection.on("ReceiveTelemetry", (data: Telemetry) => {
          dronesRef.current[data.deviceId]   = data;
          lastSeenRef.current[data.deviceId] = Date.now();

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

    const renderInterval = setInterval(() => {
        setDrones({ ...dronesRef.current });
        setPaths({ ...pathsRef.current });
        setLastSeen({ ...lastSeenRef.current });
    }, 100); 

    return () => { 
        connection.stop(); 
        clearInterval(renderInterval);
    };
  }, []);

  return (
    <div style={{ height: "100vh", width: "100vw", background: '#f1f5f9', color: '#0f172a', fontFamily: 'monospace' }}>
      
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
            ) : (
                <button onClick={() => setIsDrawMode(true)} style={{ background: '#3b82f6', color: 'white', border: 'none', padding: '6px 12px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}>
                    + Draw Patrol Route
                </button>
            )}
        </div>

        <div style={{ fontWeight: 'bold', color: status === "LINK_OK" ? "#16a34a" : "#dc2626" }}>
          SIGNAL: {status} | DRONES: {Object.keys(drones).length}
        </div>
      </header>

      {/* Swarm Telemetry Sidebar */}
      <aside style={{ position: 'absolute', left: 20, top: 80, width: 240, zIndex: 1000, background: '#ffffff', border: '1px solid #e2e8f0', padding: '20px', borderRadius: '8px', boxShadow: '0 10px 15px -3px rgba(0,0,0,0.1)', maxHeight: '80vh', overflowY: 'auto' }}>
        <h4 style={{ margin: '0 0 15px 0', fontSize: '0.75rem', color: '#64748b' }}>SWARM_TELEMETRY</h4>
        {Object.values(drones).length === 0 && <span style={{ color: '#94a3b8' }}>AWAITING_SWARM...</span>}
        
        {Object.values(drones).map(drone => {
          const stale    = (Date.now() - (lastSeen[drone.deviceId] ?? 0)) > 2000;
          const batColor = !drone.batteryVoltage || drone.batteryVoltage === 0
            ? '#94a3b8'
            : drone.batteryVoltage < 20.0 ? '#dc2626'
            : drone.batteryVoltage < 21.6 ? '#f59e0b'
            : '#16a34a';
          return (
            <div key={drone.deviceId} style={{ fontSize: '0.8rem', borderBottom: '1px solid #f1f5f9', paddingBottom: '10px', marginBottom: '10px', opacity: stale ? 0.45 : 1 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '4px' }}>
                <span>
                  ID: <strong style={{ color: stale ? '#94a3b8' : '#3b82f6' }}>{drone.deviceId.substring(0,6)}</strong>
                  {stale && <span style={{ color: '#f59e0b', marginLeft: '6px', fontSize: '0.65rem' }}>STALE</span>}
                </span>
                <button
                  onClick={() => issueRTL(drone.deviceId)}
                  style={{ background: '#dc2626', color: 'white', border: 'none', padding: '3px 7px', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold', fontSize: '0.65rem' }}>
                  RTL
                </button>
              </div>
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

      <MapContainer center={[39.586514, -9.021444]} zoom={14} zoomControl={false} style={{ height: "100%", width: "100%", cursor: isDrawMode ? 'crosshair' : 'grab' }}>
        <TileLayer url="https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png" />
        
        {/* NEW: Map Click Listener */}
        <MapClickHandler isDrawing={isDrawMode} onMapClick={(coord) => setNewRoute([...newRoute, coord])} />

        {/* NEW: Draw the planned route preview */}
        {newRoute.length > 0 && (
            <Polyline positions={newRoute} pathOptions={{ color: '#ef4444', weight: 4, dashArray: '10, 10' }} />
        )}
        {newRoute.map((coord, idx) => (
            <Marker key={`wp-${idx}`} position={coord} icon={WaypointIcon} />
        ))}

        {/* Existing: Draw Drone Trails */}
        {Object.entries(paths).map(([id, path]) => (
            <Polyline key={id} positions={path} pathOptions={{ color: '#3b82f6', weight: 3, opacity: 0.4 }} />
        ))}

        {/* Existing: Draw Drones */}
        {Object.entries(drones).map(([id, data]) => (
            <Marker key={id} position={[data.latitude, data.longitude]} icon={TacticalIcon}>
                <Popup>DRONE_ID: {id}</Popup>
            </Marker>
        ))}
      </MapContainer>
    </div>
  );
}

export default App;