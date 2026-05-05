// SPDX-License-Identifier: MIT
// Aether Protocol Serialization

#include <stdlib.h>
#include <string.h>
#include <time.h>
#if !defined(_WIN32)
// arpa/inet.h was historically included for htons/htonl helpers, but the actual
// serializer uses its own little-endian helpers below. Skip on Windows where the
// header doesn't exist; non-Windows builds keep it as a no-op include for parity.
#include <arpa/inet.h>
#endif

#include "aether/protocol.h"
#include "aether/security.h"

// ─── Cross-platform "current time in milliseconds since epoch" ─────
// MSVC's CRT does not expose POSIX `clock_gettime`. Use timespec_get (C11)
// which is available on every platform's stdc11 runtime.
static int aether_now_timespec(struct timespec *ts) {
    return timespec_get(ts, TIME_UTC) == TIME_UTC ? 0 : -1;
}
#define clock_gettime(_clk, _ts) aether_now_timespec(_ts)
#ifndef CLOCK_REALTIME
#define CLOCK_REALTIME 0
#endif

/**
 * Helper: Read little-endian uint16
 */
static uint16_t read_le_u16(const uint8_t *data) {
    return (uint16_t)data[0] | ((uint16_t)data[1] << 8);
}

/**
 * Helper: Read little-endian int32
 */
static int32_t read_le_i32(const uint8_t *data) {
    return (int32_t)data[0] | ((int32_t)data[1] << 8) | ((int32_t)data[2] << 16) | ((int32_t)data[3] << 24);
}

/**
 * Helper: Read little-endian int64
 */
static int64_t read_le_i64(const uint8_t *data) {
    return (int64_t)data[0] | ((int64_t)data[1] << 8) | ((int64_t)data[2] << 16) | ((int64_t)data[3] << 24) |
           ((int64_t)data[4] << 32) | ((int64_t)data[5] << 40) | ((int64_t)data[6] << 48) | ((int64_t)data[7] << 56);
}

/**
 * Helper: Write little-endian uint16
 */
static void write_le_u16(uint8_t *data, uint16_t value) {
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
}

/**
 * Helper: Write little-endian int32
 */
static void write_le_i32(uint8_t *data, int32_t value) {
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
    data[2] = (uint8_t)(value >> 16);
    data[3] = (uint8_t)(value >> 24);
}

/**
 * Helper: Write little-endian int64
 */
static void write_le_i64(uint8_t *data, int64_t value) {
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
    data[2] = (uint8_t)(value >> 16);
    data[3] = (uint8_t)(value >> 24);
    data[4] = (uint8_t)(value >> 32);
    data[5] = (uint8_t)(value >> 40);
    data[6] = (uint8_t)(value >> 48);
    data[7] = (uint8_t)(value >> 56);
}

/**
 * Create a new mesh packet with defaults.
 */
aether_mesh_packet_t *aether_packet_new(void) {
    aether_mesh_packet_t *packet = (aether_mesh_packet_t *)malloc(sizeof(aether_mesh_packet_t));
    if (!packet) return NULL;

    memset(packet, 0, sizeof(aether_mesh_packet_t));

    // Set defaults
    packet->protocol_version = AETHER_PROTOCOL_VERSION_CURRENT;
    packet->ttl = AETHER_DEFAULT_TTL;
    packet->priority = 0;

    // Generate random nonce
    if (!aether_random_bytes(packet->packet_nonce, AETHER_PACKET_NONCE_SIZE)) {
        free(packet);
        return NULL;
    }

    // Generate random packet ID
    if (!aether_random_bytes(packet->packet_id, AETHER_PACKET_ID_SIZE)) {
        free(packet);
        return NULL;
    }

    // Set timestamp to now (milliseconds since epoch)
    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0) {
        free(packet);
        return NULL;
    }
    packet->timestamp_ms = (int64_t)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;

    return packet;
}

/**
 * Free a mesh packet.
 */
void aether_packet_free(aether_mesh_packet_t *packet) {
    if (!packet) return;

    if (packet->source_uhid) {
        aether_zeroize(packet->source_uhid, packet->source_uhid_len);
        free(packet->source_uhid);
    }

    if (packet->destination_uhid) {
        aether_zeroize(packet->destination_uhid, packet->destination_uhid_len);
        free(packet->destination_uhid);
    }

    if (packet->payload) {
        aether_zeroize(packet->payload, packet->payload_len);
        free(packet->payload);
    }

    if (packet->signature) {
        aether_zeroize(packet->signature, packet->signature_len);
        free(packet->signature);
    }

    free(packet);
}

/**
 * Clone a packet.
 */
aether_mesh_packet_t *aether_packet_clone(const aether_mesh_packet_t *packet) {
    if (!packet) return NULL;

    aether_mesh_packet_t *clone = (aether_mesh_packet_t *)malloc(sizeof(aether_mesh_packet_t));
    if (!clone) return NULL;

    // Copy fixed fields
    memcpy(clone, packet, sizeof(aether_mesh_packet_t));

    // Clone variable fields
    if (packet->source_uhid) {
        clone->source_uhid = (char *)malloc(packet->source_uhid_len + 1);
        if (!clone->source_uhid) {
            free(clone);
            return NULL;
        }
        memcpy(clone->source_uhid, packet->source_uhid, packet->source_uhid_len);
        clone->source_uhid[packet->source_uhid_len] = '\0';
    }

    if (packet->destination_uhid) {
        clone->destination_uhid = (char *)malloc(packet->destination_uhid_len + 1);
        if (!clone->destination_uhid) {
            aether_packet_free(clone);
            return NULL;
        }
        memcpy(clone->destination_uhid, packet->destination_uhid, packet->destination_uhid_len);
        clone->destination_uhid[packet->destination_uhid_len] = '\0';
    }

    if (packet->payload) {
        clone->payload = (uint8_t *)malloc(packet->payload_len);
        if (!clone->payload) {
            aether_packet_free(clone);
            return NULL;
        }
        memcpy(clone->payload, packet->payload, packet->payload_len);
    }

    if (packet->signature) {
        clone->signature = (uint8_t *)malloc(packet->signature_len);
        if (!clone->signature) {
            aether_packet_free(clone);
            return NULL;
        }
        memcpy(clone->signature, packet->signature, packet->signature_len);
    }

    return clone;
}

/**
 * Serialize a packet to binary format.
 */
int aether_packet_serialize(const aether_mesh_packet_t *packet,
                           uint8_t *buffer,
                           size_t buffer_len) {
    if (!packet || !buffer) return -1;

    size_t required = aether_packet_estimate_size(packet);
    if (required > buffer_len) return -1;

    size_t offset = 0;

    // Protocol version [1]
    buffer[offset++] = packet->protocol_version;

    // Packet type [1]
    buffer[offset++] = packet->type;

    // Packet ID [16]
    memcpy(&buffer[offset], packet->packet_id, AETHER_PACKET_ID_SIZE);
    offset += AETHER_PACKET_ID_SIZE;

    // Priority [1]
    buffer[offset++] = packet->priority;

    // TTL [4] little-endian
    write_le_i32(&buffer[offset], (int32_t)packet->ttl);
    offset += 4;

    // TimestampMs [8] little-endian
    write_le_i64(&buffer[offset], packet->timestamp_ms);
    offset += 8;

    // SourceUhid length [2] + data [N]
    uint16_t source_len = packet->source_uhid ? packet->source_uhid_len : 0;
    write_le_u16(&buffer[offset], source_len);
    offset += 2;
    if (source_len > 0 && packet->source_uhid) {
        memcpy(&buffer[offset], packet->source_uhid, source_len);
        offset += source_len;
    }

    // DestinationUhid length [2] + data [N]
    uint16_t dest_len = packet->destination_uhid ? packet->destination_uhid_len : 0;
    write_le_u16(&buffer[offset], dest_len);
    offset += 2;
    if (dest_len > 0 && packet->destination_uhid) {
        memcpy(&buffer[offset], packet->destination_uhid, dest_len);
        offset += dest_len;
    }

    // PacketNonce length [2] + data [N]
    uint16_t nonce_len = AETHER_PACKET_NONCE_SIZE;
    write_le_u16(&buffer[offset], nonce_len);
    offset += 2;
    memcpy(&buffer[offset], packet->packet_nonce, nonce_len);
    offset += nonce_len;

    // Payload length [4] + data [N]
    int32_t payload_len = packet->payload ? (int32_t)packet->payload_len : 0;
    write_le_i32(&buffer[offset], payload_len);
    offset += 4;
    if (payload_len > 0 && packet->payload) {
        memcpy(&buffer[offset], packet->payload, packet->payload_len);
        offset += packet->payload_len;
    }

    // Signature length [2] + data [N]
    uint16_t sig_len = packet->signature ? packet->signature_len : 0;
    write_le_u16(&buffer[offset], sig_len);
    offset += 2;
    if (sig_len > 0 && packet->signature) {
        memcpy(&buffer[offset], packet->signature, sig_len);
        offset += sig_len;
    }

    return (int)offset;
}

/**
 * Deserialize a packet from binary format.
 */
aether_mesh_packet_t *aether_packet_deserialize(const uint8_t *data,
                                               size_t data_len) {
    if (!data || data_len < AETHER_MIN_PACKET_SIZE) return NULL;

    aether_mesh_packet_t *packet = (aether_mesh_packet_t *)malloc(sizeof(aether_mesh_packet_t));
    if (!packet) return NULL;

    memset(packet, 0, sizeof(aether_mesh_packet_t));

    size_t offset = 0;

    // Protocol version [1]
    if (offset >= data_len) goto error;
    packet->protocol_version = data[offset++];

    // Packet type [1]
    if (offset >= data_len) goto error;
    packet->type = data[offset++];

    // Packet ID [16]
    if (offset + AETHER_PACKET_ID_SIZE > data_len) goto error;
    memcpy(packet->packet_id, &data[offset], AETHER_PACKET_ID_SIZE);
    offset += AETHER_PACKET_ID_SIZE;

    // Priority [1]
    if (offset >= data_len) goto error;
    packet->priority = data[offset++];

    // TTL [4] — wire is little-endian int32; field is now int32_t, do not narrow to uint8_t
    if (offset + 4 > data_len) goto error;
    packet->ttl = read_le_i32(&data[offset]);
    offset += 4;

    // TimestampMs [8]
    if (offset + 8 > data_len) goto error;
    packet->timestamp_ms = read_le_i64(&data[offset]);
    offset += 8;

    // SourceUhid [2 + N]
    if (offset + 2 > data_len) goto error;
    uint16_t source_len = read_le_u16(&data[offset]);
    offset += 2;
    if (offset + source_len > data_len) goto error;
    if (source_len > AETHER_MAX_UHID_LEN) goto error;
    if (source_len > 0) {
        packet->source_uhid = (char *)malloc(source_len + 1);
        if (!packet->source_uhid) goto error;
        memcpy(packet->source_uhid, &data[offset], source_len);
        packet->source_uhid[source_len] = '\0';
        packet->source_uhid_len = source_len;
        offset += source_len;
    }

    // DestinationUhid [2 + N]
    if (offset + 2 > data_len) goto error;
    uint16_t dest_len = read_le_u16(&data[offset]);
    offset += 2;
    if (offset + dest_len > data_len) goto error;
    if (dest_len > AETHER_MAX_UHID_LEN) goto error;
    if (dest_len > 0) {
        packet->destination_uhid = (char *)malloc(dest_len + 1);
        if (!packet->destination_uhid) goto error;
        memcpy(packet->destination_uhid, &data[offset], dest_len);
        packet->destination_uhid[dest_len] = '\0';
        packet->destination_uhid_len = dest_len;
        offset += dest_len;
    }

    // PacketNonce [2 + N]
    if (offset + 2 > data_len) goto error;
    uint16_t nonce_len = read_le_u16(&data[offset]);
    offset += 2;
    if (nonce_len != AETHER_PACKET_NONCE_SIZE) goto error;
    if (offset + nonce_len > data_len) goto error;
    memcpy(packet->packet_nonce, &data[offset], nonce_len);
    offset += nonce_len;

    // Payload [4 + N]
    if (offset + 4 > data_len) goto error;
    int32_t payload_len = read_le_i32(&data[offset]);
    offset += 4;
    if (payload_len < 0 || (size_t)payload_len > AETHER_MAX_PAYLOAD_LEN) goto error;
    if (offset + (size_t)payload_len > data_len) goto error;
    if (payload_len > 0) {
        packet->payload = (uint8_t *)malloc(payload_len);
        if (!packet->payload) goto error;
        memcpy(packet->payload, &data[offset], payload_len);
        packet->payload_len = (uint32_t)payload_len;
        offset += payload_len;
    }

    // Signature [2 + N]
    if (offset + 2 > data_len) goto error;
    uint16_t sig_len = read_le_u16(&data[offset]);
    offset += 2;
    if (offset + sig_len > data_len) goto error;
    if (sig_len > 0) {
        packet->signature = (uint8_t *)malloc(sig_len);
        if (!packet->signature) goto error;
        memcpy(packet->signature, &data[offset], sig_len);
        packet->signature_len = sig_len;
        offset += sig_len;
    }

    return packet;

error:
    aether_packet_free(packet);
    return NULL;
}

/**
 * Get signable data for Ed25519 signing.
 */
uint8_t *aether_packet_get_signable_data(const aether_mesh_packet_t *packet,
                                        size_t *out_len) {
    if (!packet || !out_len) return NULL;

    // Calculate payload hash
    uint8_t payload_hash[AETHER_SHA256_SIZE];
    if (packet->payload && packet->payload_len > 0) {
        if (!aether_sha256(packet->payload, packet->payload_len, payload_hash)) {
            return NULL;
        }
    } else {
        // Hash of empty data
        if (!aether_sha256(NULL, 0, payload_hash)) {
            return NULL;
        }
    }

    uint16_t source_len = packet->source_uhid ? packet->source_uhid_len : 0;
    uint16_t dest_len = packet->destination_uhid ? packet->destination_uhid_len : 0;

    // Calculate total size
    size_t total_size = 8 +                    // nonce
                       8 +                    // timestamp
                       4 +                    // type
                       4 +                    // source_len
                       source_len +           // source_uhid
                       4 +                    // dest_len
                       dest_len +             // dest_uhid
                       AETHER_SHA256_SIZE +   // payload_hash
                       4 +                    // ttl
                       4;                     // priority

    uint8_t *signable = (uint8_t *)malloc(total_size);
    if (!signable) return NULL;

    size_t offset = 0;

    // PacketNonce [8]
    memcpy(&signable[offset], packet->packet_nonce, AETHER_PACKET_NONCE_SIZE);
    offset += AETHER_PACKET_NONCE_SIZE;

    // TimestampMs [8] little-endian
    write_le_i64(&signable[offset], packet->timestamp_ms);
    offset += 8;

    // Type [4] little-endian
    write_le_i32(&signable[offset], (int32_t)packet->type);
    offset += 4;

    // SourceUhidLength [4] little-endian
    write_le_i32(&signable[offset], (int32_t)source_len);
    offset += 4;

    // SourceUhid [N]
    if (source_len > 0 && packet->source_uhid) {
        memcpy(&signable[offset], packet->source_uhid, source_len);
        offset += source_len;
    }

    // DestinationUhidLength [4] little-endian
    write_le_i32(&signable[offset], (int32_t)dest_len);
    offset += 4;

    // DestinationUhid [N]
    if (dest_len > 0 && packet->destination_uhid) {
        memcpy(&signable[offset], packet->destination_uhid, dest_len);
        offset += dest_len;
    }

    // SHA-256(Payload) [32]
    memcpy(&signable[offset], payload_hash, AETHER_SHA256_SIZE);
    offset += AETHER_SHA256_SIZE;

    // Ttl [4] little-endian
    write_le_i32(&signable[offset], (int32_t)packet->ttl);
    offset += 4;

    // Priority [4] little-endian
    write_le_i32(&signable[offset], (int32_t)packet->priority);
    offset += 4;

    *out_len = total_size;
    return signable;
}

/**
 * Check if packet is expired.
 */
bool aether_packet_is_expired(const aether_mesh_packet_t *packet,
                             int max_age_seconds) {
    if (!packet) return true;

    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0) return true;

    int64_t now_ms = (int64_t)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;
    int64_t age_ms = now_ms - packet->timestamp_ms;
    int64_t max_age_ms = (int64_t)max_age_seconds * 1000LL;

    return age_ms > max_age_ms;
}

/**
 * Check if packet can be forwarded.
 */
bool aether_packet_can_forward(const aether_mesh_packet_t *packet) {
    if (!packet) return false;
    return packet->ttl > 0;
}

/**
 * Set source UHID.
 */
bool aether_packet_set_source_uhid(aether_mesh_packet_t *packet,
                                  const char *uhid) {
    if (!packet || !uhid) return false;

    size_t uhid_len = strlen(uhid);
    if (uhid_len > AETHER_MAX_UHID_LEN) return false;

    if (packet->source_uhid) {
        free(packet->source_uhid);
    }

    packet->source_uhid = (char *)malloc(uhid_len + 1);
    if (!packet->source_uhid) return false;

    strcpy(packet->source_uhid, uhid);
    packet->source_uhid_len = (uint16_t)uhid_len;
    return true;
}

/**
 * Set destination UHID.
 */
bool aether_packet_set_destination_uhid(aether_mesh_packet_t *packet,
                                       const char *uhid) {
    if (!packet || !uhid) return false;

    size_t uhid_len = strlen(uhid);
    if (uhid_len > AETHER_MAX_UHID_LEN) return false;

    if (packet->destination_uhid) {
        free(packet->destination_uhid);
    }

    packet->destination_uhid = (char *)malloc(uhid_len + 1);
    if (!packet->destination_uhid) return false;

    strcpy(packet->destination_uhid, uhid);
    packet->destination_uhid_len = (uint16_t)uhid_len;
    return true;
}

/**
 * Set payload.
 */
bool aether_packet_set_payload(aether_mesh_packet_t *packet,
                              const uint8_t *data,
                              size_t len) {
    if (!packet) return false;
    if (len > AETHER_MAX_PAYLOAD_LEN) return false;

    if (packet->payload) {
        aether_zeroize(packet->payload, packet->payload_len);
        free(packet->payload);
    }

    if (len == 0) {
        packet->payload = NULL;
        packet->payload_len = 0;
        return true;
    }

    packet->payload = (uint8_t *)malloc(len);
    if (!packet->payload) return false;

    memcpy(packet->payload, data, len);
    packet->payload_len = (uint32_t)len;
    return true;
}

/**
 * Set signature.
 */
bool aether_packet_set_signature(aether_mesh_packet_t *packet,
                                const uint8_t *sig,
                                size_t len) {
    if (!packet) return false;

    if (packet->signature) {
        aether_zeroize(packet->signature, packet->signature_len);
        free(packet->signature);
    }

    if (len == 0) {
        packet->signature = NULL;
        packet->signature_len = 0;
        return true;
    }

    packet->signature = (uint8_t *)malloc(len);
    if (!packet->signature) return false;

    memcpy(packet->signature, sig, len);
    packet->signature_len = (uint16_t)len;
    return true;
}

/**
 * Estimate serialized size.
 */
size_t aether_packet_estimate_size(const aether_mesh_packet_t *packet) {
    if (!packet) return 0;

    size_t size = 1 +  // protocol_version
                  1 +  // type
                  16 + // packet_id
                  1 +  // priority
                  4 +  // ttl
                  8 +  // timestamp_ms
                  2 + (packet->source_uhid ? packet->source_uhid_len : 0) +
                  2 + (packet->destination_uhid ? packet->destination_uhid_len : 0) +
                  2 + AETHER_PACKET_NONCE_SIZE +
                  4 + (packet->payload ? packet->payload_len : 0) +
                  2 + (packet->signature ? packet->signature_len : 0);

    return size;
}
