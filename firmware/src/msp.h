#pragma once
#include <Arduino.h>

// ================================================================
// MSP (MultiWii Serial Protocol) — Command IDs
// ================================================================
#define MSP_RAW_GPS     106   // Request GPS position, speed, sats
#define MSP_ATTITUDE    108   // Request roll, pitch, yaw
#define MSP_ANALOG      110   // Request battery voltage, RSSI
#define MSP_SET_RAW_RC  200   // Override RC channels (requires
                               // msp_override_channels in Betaflight CLI)

// GPS fix type constants
#define GPS_FIX_NONE    0
#define GPS_FIX_2D      1
#define GPS_FIX_3D      2

// ================================================================
// Telemetry Data Structs
// ================================================================

struct MspGps {
    uint8_t  fixType;
    uint8_t  numSat;
    int32_t  lat;          // degrees × 1e7 (raw Betaflight units)
    int32_t  lon;          // degrees × 1e7 (raw Betaflight units)
    uint16_t alt;          // metres MSL
    uint16_t speed;        // cm/s ground speed
    uint16_t groundCourse; // degrees × 10
    bool     valid;        // true only when fix ≥ 3D and sats ≥ GPS_MIN_SATS
};

struct MspAttitude {
    int16_t roll;   // decidegrees  (-1800 to +1800)
    int16_t pitch;  // decidegrees  (-900  to +900)
    int16_t yaw;    // degrees      (0 to 360)
    bool    valid;
};

struct MspAnalog {
    uint8_t  vbat;  // volts × 10  (e.g. 224 = 22.4V on 6S)
    uint16_t rssi;  // 0–1023
    bool     valid;
};

// ================================================================
// MSP Driver Class
// ================================================================
class MSP {
public:
    void begin(HardwareSerial& serial, uint32_t baud, int rxPin, int txPin);

    MspGps      requestGps();
    MspAttitude requestAttitude();
    MspAnalog   requestAnalog();

    // Sends all 8 RC channels. Requires msp_override_channels to be
    // configured in Betaflight CLI for the override to take effect
    // without disabling the primary ELRS receiver.
    bool sendRawRc(uint16_t channels[], uint8_t count);

private:
    HardwareSerial* _serial = nullptr;

    void    sendRequest(uint8_t command,
                        uint8_t* payload    = nullptr,
                        uint8_t  payloadSize = 0);

    bool    readResponse(uint8_t  expectedCommand,
                         uint8_t* buffer,
                         uint8_t& outSize,
                         uint32_t timeoutMs = 50);

    uint8_t calcChecksum(uint8_t  size,
                         uint8_t  command,
                         uint8_t* payload,
                         uint8_t  payloadLen);
};
