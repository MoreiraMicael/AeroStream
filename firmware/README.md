# AeroStream — ESP32 Telemetry Bridge Firmware

This firmware runs on the ESP32 mounted inside the Volador II VD5 airframe. It bridges the SpeedyBee F405 V5 flight controller (via MSP over UART4) to the AeroStream GCS backend (via HTTP over Wi-Fi).

---

## Prerequisites

- [PlatformIO](https://platformio.org/) (VS Code extension or CLI)
- ESP32 DevKit or bare ESP32 module
- USB cable for initial flash

---

## Hardware Wiring

```
SpeedyBee F405 V5          ESP32
──────────────────         ──────────────
UART4 TX  ──────────────►  GPIO 16  (RX1)
UART4 RX  ◄──────────────  GPIO 17  (TX1)
5V BEC    ──────────────►  5V
GND       ──────────────►  GND
```

> **Power note:** The 5V rail on the F405 V5 supplies the ESP32 alongside the ELRS receiver (100mA) and the M10 GPS (50mA). The ESP32 peaks at 500mA during Wi-Fi TX. Total peak load is 650mA against a 1500mA BEC — within spec, but do **not** add any further 5V loads (e.g. LED strips) to this build.

---

## Betaflight Configuration

### 1. Enable MSP on UART4

In **Betaflight Configurator → Ports tab**:

| UART   | Configuration | Baud  |
|--------|---------------|-------|
| UART4  | MSP           | 115200 |

### 2. Enable MSP RC Override for GPS Rescue

In the **CLI tab**, run:

```
set msp_override_channels = 128
save
```

`128` is the bitmask for RC channel 8 (AUX4). This allows the ESP32 to assert GPS Rescue on AUX4 via MSP without replacing the primary ELRS receiver on the other channels.

### 3. Configure GPS Rescue Mode

In **Modes tab**, assign **GPS RESCUE** to AUX4 with a range of **1700–2100**.

### 4. Verify GPS Rescue settings

In **Failsafe tab**, configure GPS Rescue parameters (minimum sats, altitude, return speed, etc.) appropriate for your flying area.

---

## Firmware Configuration

Edit `src/config.h` before flashing:

```cpp
#define WIFI_SSID           "your_ground_network"
#define WIFI_PASSWORD       "your_password"
#define GCS_HOST            "192.168.1.X"   // IP of the Docker host
```

All other parameters are documented inline in `config.h`.

---

## Flashing

```bash
cd firmware
pio run --target upload
pio device monitor   # Watch boot sequence and telemetry log
```

Expected boot output:
```
[NVS] First boot — UUID generated and stored: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
[WiFi] Connected  IP: 192.168.1.42  TX: 10 dBm
[SNTP] Synced: 2026-04-17T10:23:01Z
[BOOT] Bridge active. Waiting for GPS fix...
[GPS]  Waiting for fix — sats visible: 4
[GPS]  Waiting for fix — sats visible: 7
... (telemetry POSTs begin once sats ≥ 6 with 3D fix)
```

---

## C2 Commands

The GCS can piggyback commands on the `202 Accepted` response:

| Command        | ESP32 Action                                      |
|----------------|---------------------------------------------------|
| `RTL`          | Asserts GPS Rescue on AUX4 via MSP RC override    |
| `UPDATE_ROUTE` | Logged and ignored (Betaflight has no waypoint engine) |

### Migration path for UPDATE_ROUTE

To enable autonomous waypoint routing, replace Betaflight with **iNav** firmware on the SpeedyBee F405 V5 (it is a supported target). Then implement MSP command `118` (`MSP_WP`) in `msp.h/cpp` to upload waypoint missions directly from the ESP32.

---

## RF Coexistence Note

The ELRS receiver (RP1 V2) and the ESP32 Wi-Fi both operate on 2.4 GHz. The firmware reduces ESP32 TX power to **10 dBm** (configurable in `config.h`) to minimise desensitisation of the ELRS link. Monitor **LQ (Link Quality)** in the OSD during early test flights and reduce `WIFI_TX_POWER_DBM` further if LQ degrades during active telemetry transmission.

---

## Conformal Coating

Apply **MG Chemicals 422B** to the ESP32 **only after** successful bench and flight testing. Coating before validation makes re-flashing impossible without solvent removal.
