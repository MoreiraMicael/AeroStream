#pragma once

// ================================================================
// AeroStream ESP32 Firmware — User Configuration
// ================================================================
// Edit this file before flashing. All other source files should
// be left untouched unless you know what you are doing.
// ================================================================

// --- Wi-Fi (your GCS ground network) ---
#define WIFI_SSID           "YOUR_GCS_NETWORK"
#define WIFI_PASSWORD       "YOUR_PASSWORD"

// --- GCS Backend ---
// IP address of the machine running the AeroStream Docker stack.
// Do NOT use localhost — the ESP32 is not on the same machine.
#define GCS_HOST            "192.168.1.X"
#define GCS_PORT            5233
#define GCS_ENDPOINT        "/telemetry"

// --- UART: Betaflight Flight Controller ---
// The ESP32 talks to the FC via UART1 (hardware serial).
// Wire FC TX → ESP32 GPIO16 (RX1)
//     FC RX → ESP32 GPIO17 (TX1)
// In Betaflight Ports tab: set this UART to "MSP" (speed 115200).
// Also enable: set msp_override_channels = 128   (in CLI) to allow
// the ESP32 to trigger GPS Rescue on AUX4 via MSP RC override.
#define FC_UART             Serial1
#define FC_BAUD             115200
#define FC_RX_PIN           16
#define FC_TX_PIN           17

// --- Wi-Fi TX Power ---
// Reducing TX power minimises 2.4GHz desensitisation of the ELRS
// receiver, which sits centimetres away on the same airframe.
// 10 dBm provides ~150m range to the GCS — sufficient for Wi-Fi
// telemetry, which is range-limited by design.
// Range: 8 (minimum) to 20 (maximum) dBm.
#define WIFI_TX_POWER_DBM   10

// --- Telemetry Rate ---
// Must match simulate.js (100ms = 10Hz). Do not change.
#define TELEMETRY_HZ        10
#define TELEMETRY_MS        (1000 / TELEMETRY_HZ)

// --- GPS Rescue AUX Channel ---
// Set this to the AUX number you assigned to GPS Rescue in
// Betaflight's Modes tab.
// AUX1 = RC channel 5 | AUX2 = ch6 | AUX3 = ch7 | AUX4 = ch8
// The CLI command to enable MSP override for AUX4 is:
//   set msp_override_channels = 128
//   save
#define GPS_RESCUE_AUX      4   // AUX4 (RC channel 8)

// --- MSP RC Channel Values ---
#define RC_MIN              1000
#define RC_MID              1500
#define RC_MAX              2000

// --- GPS Lock Requirement ---
// Minimum satellites required before telemetry is transmitted.
// Prevents the GCS map from placing the drone at 0°,0° (null island).
#define GPS_MIN_SATS        6
