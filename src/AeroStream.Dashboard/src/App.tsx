import { useEffect, useState, useRef } from 'react';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { MapContainer, TileLayer, Marker, Popup, Polyline } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';
import L from 'leaflet';

// Tactical Icon Fix
const TacticalIcon = L.icon({
    iconUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-icon.png',
    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.7.1/images/marker-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34]
});

interface Telemetry {
  deviceId: string;
  latitude: number;
  longitude: number;
  altitude: number;
  speed: number;
  timestamp: string;
}

function App() {
  // 1. THE BUFFER: We store data here to prevent React from re-rendering 100 times a second.
  const dronesRef = useRef<Record<string, Telemetry>>({});
  const pathsRef = useRef<Record<string, [number, number][]>>({});

  // 2. THE UI STATE: This is what the user actually sees on screen.
  const [drones, setDrones] = useState<Record<string, Telemetry>>({});
  const [paths, setPaths] = useState<Record<string, [number, number][]>>({});
  const [status, setStatus] = useState("DISCONNECTED");

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("http://localhost:5233/telemetryHub")
      .withAutomaticReconnect()
      .build();

    connection.start()
      .then(() => {
        setStatus("LINK_OK");
        connection.on("ReceiveTelemetry", (data: Telemetry) => {
          // Instantly buffer incoming data (Zero UI cost)
          dronesRef.current[data.deviceId] = data;

          if (!pathsRef.current[data.deviceId]) {
            pathsRef.current[data.deviceId] = [];
          }
          
          // Add to path, but limit to 50 points per drone to prevent memory leaks
          pathsRef.current[data.deviceId].push([data.latitude, data.longitude]);
          if (pathsRef.current[data.deviceId].length > 50) {
             pathsRef.current[data.deviceId].shift();
          }
        });
      })
      .catch(() => setStatus("LINK_LOST"));

    // 3. THE RENDER LOOP: Update the UI at a safe 10 Frames Per Second
    const renderInterval = setInterval(() => {
        setDrones({ ...dronesRef.current });
        setPaths({ ...pathsRef.current });
    }, 100); // 100ms = 10 FPS

    return () => { 
        connection.stop(); 
        clearInterval(renderInterval);
    };
  }, []);

  return (
    <div style={{ height: "100vh", width: "100vw", background: '#f1f5f9', color: '#0f172a', fontFamily: 'monospace' }}>
      
      <header style={{ position: 'absolute', top: 0, width: '100%', zIndex: 1000, background: '#ffffff', borderBottom: '2px solid #3b82f6', padding: '12px 20px', display: 'flex', justifyContent: 'space-between', boxShadow: '0 2px 4px rgba(0,0,0,0.1)' }}>
        <div style={{ fontWeight: 'bold' }}>
          <span style={{ color: '#3b82f6' }}>AEROSTREAM</span> // SWARM_COMMAND
        </div>
        <div style={{ fontWeight: 'bold', color: status === "LINK_OK" ? "#16a34a" : "#dc2626" }}>
          SIGNAL: {status} | DRONES: {Object.keys(drones).length}
        </div>
      </header>

      {/* Swarm Telemetry Sidebar */}
      <aside style={{ position: 'absolute', left: 20, top: 80, width: 240, zIndex: 1000, background: '#ffffff', border: '1px solid #e2e8f0', padding: '20px', borderRadius: '8px', boxShadow: '0 10px 15px -3px rgba(0,0,0,0.1)', maxHeight: '80vh', overflowY: 'auto' }}>
        <h4 style={{ margin: '0 0 15px 0', fontSize: '0.75rem', color: '#64748b' }}>SWARM_TELEMETRY</h4>
        {Object.values(drones).length === 0 && <span style={{ color: '#94a3b8' }}>AWAITING_SWARM...</span>}
        
        {Object.values(drones).map(drone => (
          <div key={drone.deviceId} style={{ fontSize: '0.8rem', borderBottom: '1px solid #f1f5f9', paddingBottom: '10px', marginBottom: '10px' }}>
            ID: <strong style={{ color: '#3b82f6' }}>{drone.deviceId.substring(0,6)}</strong><br/>
            ALT: {drone.altitude.toFixed(1)}m | SPD: {drone.speed.toFixed(0)}km/h
          </div>
        ))}
      </aside>

      {/* The Map (Auto-Center removed to stop camera thrashing) */}
      <MapContainer center={[39.36, -9.14]} zoom={14} zoomControl={false} style={{ height: "100%", width: "100%" }}>
        <TileLayer url="https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png" />
        
        {/* Draw Trails */}
        {Object.entries(paths).map(([id, path]) => (
            <Polyline key={id} positions={path} pathOptions={{ color: '#3b82f6', weight: 3, opacity: 0.4 }} />
        ))}

        {/* Draw Drones */}
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