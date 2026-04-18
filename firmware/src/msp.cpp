#include "msp.h"
#include "config.h"

// ================================================================
// Initialisation
// ================================================================

void MSP::begin(HardwareSerial& serial, uint32_t baud, int rxPin, int txPin) {
    _serial = &serial;
    serial.begin(baud, SERIAL_8N1, rxPin, txPin);
}

// ================================================================
// Private Helpers
// ================================================================

uint8_t MSP::calcChecksum(uint8_t size, uint8_t command,
                           uint8_t* payload, uint8_t payloadLen) {
    uint8_t crc = size ^ command;
    for (uint8_t i = 0; i < payloadLen; i++) {
        crc ^= payload[i];
    }
    return crc;
}

void MSP::sendRequest(uint8_t command, uint8_t* payload, uint8_t payloadSize) {
    uint8_t checksum = calcChecksum(payloadSize, command, payload, payloadSize);
    _serial->write('$');
    _serial->write('M');
    _serial->write('<');
    _serial->write(payloadSize);
    _serial->write(command);
    if (payload && payloadSize > 0) {
        _serial->write(payload, payloadSize);
    }
    _serial->write(checksum);
}

bool MSP::readResponse(uint8_t expectedCommand, uint8_t* buffer,
                        uint8_t& outSize, uint32_t timeoutMs) {
    uint32_t deadline = millis() + timeoutMs;

    // Lightweight state machine — avoids blocking the loop
    enum State : uint8_t {
        WAIT_DOLLAR, WAIT_M, WAIT_DIR,
        WAIT_SIZE, WAIT_CMD, WAIT_PAYLOAD, WAIT_CRC
    };
    State   state      = WAIT_DOLLAR;
    uint8_t payloadSize = 0;
    uint8_t command     = 0;
    uint8_t idx         = 0;
    uint8_t checksum    = 0;

    while (millis() < deadline) {
        if (!_serial->available()) {
            delayMicroseconds(100);
            continue;
        }
        uint8_t c = (uint8_t)_serial->read();

        switch (state) {
            case WAIT_DOLLAR:  if (c == '$') state = WAIT_M;       break;
            case WAIT_M:       if (c == 'M') state = WAIT_DIR;
                               else          state = WAIT_DOLLAR;  break;
            case WAIT_DIR:     if (c == '>') state = WAIT_SIZE;
                               else          state = WAIT_DOLLAR;  break;

            case WAIT_SIZE:
                payloadSize = c;
                checksum    = c;
                state       = WAIT_CMD;
                break;

            case WAIT_CMD:
                command   = c;
                checksum ^= c;
                if (command != expectedCommand) {
                    // Different command in the pipe — discard and re-sync
                    state = WAIT_DOLLAR;
                } else if (payloadSize == 0) {
                    state = WAIT_CRC;
                } else {
                    idx   = 0;
                    state = WAIT_PAYLOAD;
                }
                break;

            case WAIT_PAYLOAD:
                buffer[idx++] = c;
                checksum     ^= c;
                if (idx >= payloadSize) state = WAIT_CRC;
                break;

            case WAIT_CRC:
                outSize = payloadSize;
                return (c == checksum); // false = corrupted frame
        }
    }
    return false; // Timeout
}

// ================================================================
// Public API
// ================================================================

MspGps MSP::requestGps() {
    MspGps result = {};
    result.valid  = false;

    sendRequest(MSP_RAW_GPS);

    uint8_t buf[32] = {};
    uint8_t size    = 0;

    if (!readResponse(MSP_RAW_GPS, buf, size) || size < 16) {
        return result;
    }

    result.fixType      = buf[0];
    result.numSat       = buf[1];

    // Betaflight transmits all multi-byte values little-endian
    result.lat = (int32_t)(
        (uint32_t)buf[2]        |
        ((uint32_t)buf[3] << 8) |
        ((uint32_t)buf[4] << 16)|
        ((uint32_t)buf[5] << 24));

    result.lon = (int32_t)(
        (uint32_t)buf[6]        |
        ((uint32_t)buf[7] << 8) |
        ((uint32_t)buf[8] << 16)|
        ((uint32_t)buf[9] << 24));

    result.alt          = (uint16_t)(buf[10] | ((uint16_t)buf[11] << 8));
    result.speed        = (uint16_t)(buf[12] | ((uint16_t)buf[13] << 8));
    result.groundCourse = (uint16_t)(buf[14] | ((uint16_t)buf[15] << 8));

    // Only flag valid when we have a genuine 3D fix with enough satellites
    result.valid = (result.fixType >= GPS_FIX_3D && result.numSat >= GPS_MIN_SATS);
    return result;
}

MspAttitude MSP::requestAttitude() {
    MspAttitude result = {};
    result.valid        = false;

    sendRequest(MSP_ATTITUDE);

    uint8_t buf[16] = {};
    uint8_t size    = 0;

    if (!readResponse(MSP_ATTITUDE, buf, size) || size < 6) {
        return result;
    }

    // Betaflight MSP_ATTITUDE order: angx (roll), angy (pitch), heading (yaw)
    result.roll  = (int16_t)(buf[0] | ((uint16_t)buf[1] << 8));
    result.pitch = (int16_t)(buf[2] | ((uint16_t)buf[3] << 8));
    result.yaw   = (int16_t)(buf[4] | ((uint16_t)buf[5] << 8));
    result.valid = true;
    return result;
}

MspAnalog MSP::requestAnalog() {
    MspAnalog result = {};
    result.valid      = false;

    sendRequest(MSP_ANALOG);

    uint8_t buf[16] = {};
    uint8_t size    = 0;

    if (!readResponse(MSP_ANALOG, buf, size) || size < 7) {
        return result;
    }

    // Byte layout: [0]=vbat  [1-2]=mAhDrawn  [3-4]=rssi  [5-6]=amperage
    result.vbat  = buf[0];
    result.rssi  = (uint16_t)(buf[3] | ((uint16_t)buf[4] << 8));
    result.valid = true;
    return result;
}

bool MSP::sendRawRc(uint16_t channels[], uint8_t count) {
    uint8_t payload[32] = {};
    uint8_t payloadSize = count * 2;

    for (uint8_t i = 0; i < count; i++) {
        payload[i * 2]     =  channels[i]       & 0xFF;
        payload[i * 2 + 1] = (channels[i] >> 8) & 0xFF;
    }

    sendRequest(MSP_SET_RAW_RC, payload, payloadSize);

    // MSP_SET_RAW_RC responds with a zero-payload ACK
    uint8_t buf[4] = {};
    uint8_t size   = 0;
    return readResponse(MSP_SET_RAW_RC, buf, size, 30);
}
