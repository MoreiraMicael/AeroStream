#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <ArduinoJson.h>
#include <Preferences.h>     // NVS (Non-Volatile Storage) wrapper
#include <esp_wifi.h>
#include "config.h"
#include "msp.h"

// ================================================================
// Global State
// ================================================================
MSP         msp;
Preferences prefs;

String  deviceUuid;
bool    rtlActive = false;

// ================================================================
// UUID — Persistent Identity
// ================================================================
// The drone must always present the same UUID to the GCS so that
// the PostgreSQL black-box log correctly associates all flights.
// UUID is generated once on first boot and stored in NVS flash.
// ================================================================

String generateUuidV4() {
    uint8_t raw[16];
    for (int i = 0; i < 16; i++) raw[i] = (uint8_t)(esp_random() & 0xFF);

    // Set version 4 and RFC 4122 variant bits
    raw[6] = (raw[6] & 0x0F) | 0x40;
    raw[8] = (raw[8] & 0x3F) | 0x80;

    char uuid[37];
    snprintf(uuid, sizeof(uuid),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        raw[0],  raw[1],  raw[2],  raw[3],
        raw[4],  raw[5],
        raw[6],  raw[7],
        raw[8],  raw[9],
        raw[10], raw[11], raw[12], raw[13], raw[14], raw[15]);

    return String(uuid);
}

void loadOrCreateUuid() {
    prefs.begin("aerostream", false);
    deviceUuid = prefs.getString("uuid", "");

    if (deviceUuid.length() == 0) {
        deviceUuid = generateUuidV4();
        prefs.putString("uuid", deviceUuid);
        Serial.println("[NVS] First boot — UUID generated and stored: " + deviceUuid);
    } else {
        Serial.println("[NVS] Loaded persistent UUID: " + deviceUuid);
    }
    prefs.end();
}

// ================================================================
// Wi-Fi — Connection & TX Power
// ================================================================

void connectWifi() {
    Serial.printf("[WiFi] Connecting to \"%s\"", WIFI_SSID);
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
    }

    // Reduce TX power to limit 2.4GHz interference with the ELRS receiver.
    // esp_wifi_set_max_tx_power() uses units of 0.25 dBm.
    esp_wifi_set_max_tx_power(WIFI_TX_POWER_DBM * 4);

    Serial.printf("\n[WiFi] Connected  IP: %s  TX: %d dBm\n",
        WiFi.localIP().toString().c_str(), WIFI_TX_POWER_DBM);
}

// ================================================================
// Time — SNTP Synchronisation
// ================================================================
// The GCS schema requires a proper ISO 8601 timestamp. The ESP32
// has no RTC, so we must sync from NTP before sending any packets.
// ================================================================

void syncTime() {
    Serial.print("[SNTP] Synchronising wall clock...");
    configTime(0, 0, "pool.ntp.org", "time.google.com");

    struct tm ti;
    int attempts = 0;
    while (!getLocalTime(&ti) && attempts++ < 30) {
        delay(500);
        Serial.print(".");
    }

    if (attempts < 30) {
        char buf[32];
        strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &ti);
        Serial.printf("\n[SNTP] Synced: %s\n", buf);
    } else {
        Serial.println("\n[SNTP] WARNING: Time sync failed. Timestamps will be incorrect.");
    }
}

String getIsoTimestamp() {
    struct tm ti;
    if (!getLocalTime(&ti)) {
        return "1970-01-01T00:00:00.000Z";
    }

    struct timeval tv;
    gettimeofday(&tv, nullptr);

    char buf[32];
    snprintf(buf, sizeof(buf),
        "%04d-%02d-%02dT%02d:%02d:%02d.%03ldZ",
        ti.tm_year + 1900,
        ti.tm_mon  + 1,
        ti.tm_mday,
        ti.tm_hour,
        ti.tm_min,
        ti.tm_sec,
        (long)(tv.tv_usec / 1000));

    return String(buf);
}

// ================================================================
// RTL Execution — MSP RC Override
// ================================================================
// Sends all 8 RC channels via MSP with the GPS Rescue AUX channel
// forced HIGH. Requires Betaflight CLI configuration:
//   set msp_override_channels = 128   (bitmask for AUX4 / ch8)
//   save
//
// This allows MSP to override AUX4 without replacing the primary
// ELRS receiver on the other channels — the key safety property.
// This function is called every telemetry cycle while rtlActive is
// true, keeping the override asserted until the FC lands.
// ================================================================

void executeRtl() {
    uint16_t channels[8];
    for (int i = 0; i < 8; i++) channels[i] = RC_MID;

    // AUX1=index 4, AUX2=index 5, AUX3=index 6, AUX4=index 7
    int idx = 3 + GPS_RESCUE_AUX;   // GPS_RESCUE_AUX is 1-based
    if (idx >= 0 && idx < 8) {
        channels[idx] = RC_MAX;
    }

    if (msp.sendRawRc(channels, 8)) {
        Serial.println("[RTL] MSP RC override asserted — GPS Rescue active.");
    } else {
        Serial.println("[RTL] WARNING: MSP RC override failed. Manual intervention required.");
    }
}

// ================================================================
// C2 Command Handler
// ================================================================
// The GCS piggybacks commands on the 202 Accepted response body.
// The ESP32 reads this body and acts accordingly.
// ================================================================

void handleCommand(const String& responseBody) {
    if (responseBody.isEmpty() || responseBody == "null") return;

    StaticJsonDocument<1024> doc;
    if (deserializeJson(doc, responseBody) != DeserializationError::Ok) return;

    const char* command = doc["command"];
    if (!command) return;

    // --- RTL: Trigger GPS Rescue via MSP RC override ---
    if (strcmp(command, "RTL") == 0) {
        if (!rtlActive) {
            Serial.println("[C2] RTL command received. Activating GPS Rescue.");
            rtlActive = true;
        }

    // --- UPDATE_ROUTE: Waypoint navigation not supported on Betaflight ---
    } else if (strcmp(command, "UPDATE_ROUTE") == 0) {
        int waypointCount = doc["data"].size();
        Serial.printf(
            "[C2] UPDATE_ROUTE received (%d waypoints). "
            "NOTE: Betaflight has no waypoint engine — command cannot be executed. "
            "To enable autonomous routing, migrate the FC to iNav firmware and "
            "implement MSP_WP (command 118) here.\n",
            waypointCount);
    }
}

// ================================================================
// Boot Banner
// ================================================================

void printBanner() {
    Serial.println();
    Serial.println(F("  ___             __ _                   "));
    Serial.println(F(" / _ \\___ _ __ / _\\ |_ _ __ ___  __ _ _ __ ___ "));
    Serial.println(F("| | | |/ _ \\ '__|\\ \\| __| '__/ _ \\/ _` | '_ ` _ \\"));
    Serial.println(F("| |_| |  __/ |  _\\ \\ |_| | |  __/ (_| | | | | | |"));
    Serial.println(F(" \\___/ \\___|_|  \\__/\\__|_|  \\___|\\__,_|_| |_| |_|"));
    Serial.println(F("  AeroStream v1.0  —  ESP32 Telemetry Bridge"));
    Serial.println();
}

// ================================================================
// Setup
// ================================================================

void setup() {
    Serial.begin(115200);
    delay(1000);

    printBanner();
    loadOrCreateUuid();

    Serial.printf("[CFG] GCS target : http://%s:%d%s\n", GCS_HOST, GCS_PORT, GCS_ENDPOINT);
    Serial.printf("[CFG] Telemetry  : %d Hz\n", TELEMETRY_HZ);
    Serial.printf("[CFG] GPS Rescue : AUX%d\n", GPS_RESCUE_AUX);

    msp.begin(FC_UART, FC_BAUD, FC_RX_PIN, FC_TX_PIN);
    connectWifi();
    syncTime();

    Serial.println("[BOOT] Bridge active. Waiting for GPS fix...\n");
}

// ================================================================
// Main Loop
// ================================================================

void loop() {
    static uint32_t lastTelemetry = 0;
    uint32_t now = millis();

    if (now - lastTelemetry < TELEMETRY_MS) return;
    lastTelemetry = now;

    // --- 1. Collect telemetry from FC ---
    MspGps      gps      = msp.requestGps();
    MspAttitude attitude = msp.requestAttitude();
    MspAnalog   analog   = msp.requestAnalog();

    // Hold transmission until we have a real 3D GPS fix.
    // This prevents the GCS map from plotting the drone at 0°, 0°.
    if (!gps.valid) {
        static uint32_t lastWarn = 0;
        if (now - lastWarn > 5000) {
            Serial.printf("[GPS] Waiting for fix — sats visible: %d\n", gps.numSat);
            lastWarn = now;
        }
        return;
    }

    // --- 2. Persist active RTL override every cycle ---
    if (rtlActive) {
        executeRtl();
    }

    // --- 3. Reconnect Wi-Fi if link was lost ---
    if (WiFi.status() != WL_CONNECTED) {
        Serial.println("[WiFi] Link lost — reconnecting...");
        connectWifi();
        return;
    }

    // --- 4. Build JSON payload ---
    // Unit conversions:
    //   lat/lon  : raw Betaflight int32 (degrees × 1e7) → double degrees
    //   speed    : cm/s → km/h  (÷ 27.778)
    //   pitch    : decidegrees  → degrees  (÷ 10)
    //   roll     : decidegrees  → degrees  (÷ 10)
    //   vbat     : volts × 10   → volts    (÷ 10)
    StaticJsonDocument<300> doc;
    doc["deviceId"]        = deviceUuid;
    doc["latitude"]        = gps.lat  / 1e7;
    doc["longitude"]       = gps.lon  / 1e7;
    doc["altitude"]        = (double)gps.alt;
    doc["speed"]           = gps.speed / 27.778;
    doc["pitch"]           = attitude.valid ? attitude.pitch / 10.0 : 0.0;
    doc["roll"]            = attitude.valid ? attitude.roll  / 10.0 : 0.0;
    doc["yaw"]             = attitude.valid ? (double)attitude.yaw  : 0.0;
    doc["batteryVoltage"]  = analog.valid   ? analog.vbat   / 10.0 : 0.0;
    doc["timestamp"]       = getIsoTimestamp();

    String payload;
    serializeJson(doc, payload);

    // --- 5. POST to GCS, read C2 response ---
    HTTPClient http;
    String url = String("http://") + GCS_HOST + ":" + GCS_PORT + GCS_ENDPOINT;
    http.begin(url);
    http.addHeader("Content-Type", "application/json");

    // Timeout must fit within one telemetry cycle (100ms).
    // 80ms leaves 20ms headroom for MSP polling overhead.
    http.setTimeout(80);

    int httpCode = http.POST(payload);

    if (httpCode == 202) {
        String response = http.getString();
        handleCommand(response);
    } else if (httpCode == 503) {
        Serial.println("[GCS] Ingestion queue full — packet dropped.");
    } else if (httpCode > 0) {
        Serial.printf("[HTTP] Unexpected status: %d\n", httpCode);
    } else {
        Serial.printf("[HTTP] Error: %s\n", http.errorToString(httpCode).c_str());
    }

    http.end();
}
