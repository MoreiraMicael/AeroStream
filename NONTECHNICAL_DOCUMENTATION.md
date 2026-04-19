# AeroStream: User Guide for Pilots & Operators

**Version:** 1.0  
**Date:** April 2026  
**Audience:** Pilots, Operators, Project Managers, Non-Technical Stakeholders

---

## What is AeroStream?

**AeroStream** is a **Ground Control Station** — the software that pilots use to fly and monitor drones.

Think of it like:
- **AeroStream** = Air Traffic Control Tower for drones
- **Pilot Dashboard** = The screen showing live drone positions, battery, and status
- **Drone** = The aircraft sending back "I'm here, here's my status"

---

## The Problem It Solves

**Drones send A LOT of data very fast.**

A single drone sends about 10 data updates per second. With 10 drones, that's 100 updates per second.

**Standard systems get overwhelmed and LOSE DATA.**

**AeroStream's Solution:** It uses a smart queue system to ensure:
1. **No data is lost** (even during peaks)
2. **Pilots see updates on screen instantly** (less than 1 second delay)
3. **All data is recorded** in a database for later review

---

## Key Features

| Feature | What It Does |
|---------|-------------|
| **Live Map** | Shows all drones on a live map with positions, altitude, battery level |
| **Multi-Drone Swarm** | Manage 10+ drones simultaneously |
| **Real-Time Updates** | Dashboard refreshes sub-second (faster than human eye) |
| **Flight History** | Every flight is recorded; review later for analysis |
| **Commands** | Send "Return Home" or "Follow This Route" |
| **Smart Routing** | Draw a route on map, send it to multiple drones at once |

---

## Daily Workflow

### Pre-Flight (5 minutes)

1. Start the system
2. Verify "Connected" status
3. Turn on drones
4. Wait 5-10 seconds for them to appear on map
5. Check battery levels (should be 100% or near)

### During Flight (Continuous Monitoring)

1. Watch live positions on map
2. Monitor altitude, speed, battery
3. Issue commands if needed
4. Note any issues

### Post-Flight (5 minutes)

1. Land all drones
2. Shut down system (optional)
3. Review flight history (optional)

---

## Dashboard Interface

```
┌─────────────────────────────────────────┐
│  AeroStream Dashboard        [Status]   │
├─────────────────────────────────────────┤
│                                         │
│  Live Map View                          │
│  ├─ Drone 1 (green) - 85% battery      │
│  ├─ Drone 2 (green) - 92% battery      │
│  ├─ Drone 3 (red)   - 15% battery      │
│  └─ ... 10 drones total                │
│                                         │
├─────────────────────────────────────────┤
│ [Commands] [Draw Route] [History]       │
└─────────────────────────────────────────┘
```

**Color Codes:**
- 🟢 **Green** — Normal operation (battery > 50%)
- 🟡 **Yellow** — Low battery (20-50%)
- 🔴 **Red** — Critical (battery < 20%, offline, or error)

---

## Managing Your Swarm

### Monitoring Multiple Drones

**Quick Status Check:**
- All green? → Safe to continue
- Yellow appearing? → Plan landing soon
- Red? → EMERGENCY; act now

### Sending Commands

**Method 1: Quick Command (Easiest)**
```
1. Click on drone marker on map
2. Select action: "Return Home" or "Land"
3. Click "Send"
```

**Method 2: Swarm Route (Advanced)**
```
1. Click "Draw Route" button
2. Click on map to place waypoints
3. Select drones to send route to
4. Click "Deploy"
```

### Available Commands

| Command | Effect |
|---------|--------|
| **RTL** | Return to Launch (go home) |
| **LAND** | Land safely |
| **EMERGENCY_STOP** | Stop all motors (emergency only) |
| **UPDATE_ROUTE** | Change flight path |

---

## Safety & Reliability

### What's Reliable

✅ **Live Position Updates** — Updates arrive in real-time  
✅ **Data Persistence** — All flights recorded permanently  
✅ **Command Delivery** — Commands reach drones >99% of time  
✅ **System Uptime** — Designed for 24/7 operation  

### What Could Fail

⚠️ **Drone Loses WiFi Signal** — System loses tracking  
⚠️ **Backend Server Crashes** — Drones continue flying (but unmonitored)  
⚠️ **Network Overload** — System might drop some packets  

### Safety Best Practices

1. **Always Monitor Dashboard** — Keep eye on map at all times
2. **Test Commands Beforehand** — Verify RTL and LAND work
3. **Have Manual Backup** — Train pilots to manual control if system fails
4. **Check Battery Regularly** — Land before battery critical (< 20%)
5. **Verify Waypoints** — Review route before deploying to swarm
6. **Test WiFi Coverage** — Fly test mission to verify signal strength

---

## Common Questions

### Q: What if the system crashes while drones are flying?

**A:** Drones continue flying. They don't stop when the system goes down. **Always have a manual backup plan.**

---

### Q: Can I fly 100 drones at once?

**A:** Theoretically yes. Practically, pilot workload limits you to 5-10 drones. For larger swarms, use autonomous flight (routes, not manual commands).

---

### Q: How long are flights recorded?

**A:** Forever (until database is manually purged). You can replay any flight conducted, months or years later.

---

### Q: What if a drone doesn't receive my command?

**A:** The command is delivered within 100-200ms if the drone is connected. If offline, it won't receive anything. Solution: Move closer, check WiFi, or manually land.

---

### Q: Can I use this system outdoors in bad weather?

**A:** Yes, as long as drones are weatherproof and WiFi signal reaches them. Rain/fog weaken signals; fly closer if needed.

---

### Q: How often is the dashboard updated?

**A:** 10 times per second (100 milliseconds). Should look smooth and fluid, not jerky.

---

### Q: Can I export flight data?

**A:** Yes (feature in development). You'll be able to download CSV or JSON with all flight data for analysis.

---

### Q: What's the maximum number of waypoints in a route?

**A:** Practically ~50 waypoints. Longer routes cause slowdown.

---

### Q: Can drones avoid obstacles automatically?

**A:** No. The system does not include obstacle avoidance. You must manually edit the route to avoid obstacles.

---

### Q: What if WiFi signal drops mid-flight?

**A:** Drones have a "failsafe" mode (check drone settings):
- Option 1: Return Home automatically
- Option 2: Land in place
- Option 3: Continue and hope signal returns

---

## Troubleshooting Guide

### Problem: Dashboard Shows "Disconnected"

**Fix:**
1. Check network connection
2. Wait 10 seconds (server might be restarting)
3. Refresh browser (Ctrl+R or Cmd+R)
4. Restart backend server

---

### Problem: Drones Don't Appear on Map

**Fix:**
1. Check drone WiFi: Verify SSID/password correct
2. Move drone closer to WiFi router
3. Wait 10 seconds
4. Refresh browser
5. Check drone firmware (update if old)

---

### Problem: Commands Don't Work

**Fix:**
1. Verify drone is connected (check marker color)
2. Move drone closer to base station
3. Retry command
4. If still failing, land drone manually
5. Check drone firmware logs for errors

---

### Problem: Dashboard Updates Are Slow or Freezing

**Fix:**
1. Close other browser tabs
2. Restart browser
3. Check network speed
4. Reduce number of drones (if possible)
5. Restart backend server

---

### Problem: Battery % Shows Wrong or Doesn't Update

**Fix:**
1. Update drone firmware
2. Refresh dashboard
3. Power cycle drone
4. Check drone battery sensor (might be broken)

---

### Problem: System Keeps Dropping Packets / Losing Data

**Fix:**
1. Reduce number of active drones
2. Move router closer (less crowded location)
3. Upgrade backend server (add CPU/RAM)

---

## Performance & Limits

### What the System Can Handle

| Metric | Limit | Notes |
|--------|-------|-------|
| **Drones Simultaneously** | 100+ | Depends on backend power |
| **Update Frequency** | 10 Hz per drone | 100ms between updates |
| **Historical Data** | Unlimited | Until database storage full |
| **Session Duration** | 24+ hours | System designed for long flights |

---

## Getting Help

**Resources:**

- **GitHub Issues:** Report bugs at project repo
- **Documentation:** Full technical docs in `TECHNICAL_DOCUMENTATION.md`
- **Support Email:** (if applicable)

---

**End of Non-Technical Documentation**
