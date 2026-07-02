// SPDX-License-Identifier: MIT
// Aether presence — PresenceBeacon (21) broadcast + PresenceQuery (22) broadcast transport.
//
// A privacy-preserving "I'm here" / "who's around here?" service. A node broadcasts a beacon
// advertising its ROTATING erid (from the ERID directory — never the stable UHID), a COARSE geohash
// (host-truncated per privacy level; "" when hidden), a capability bitmask, a presence status, and a
// send timestamp; or it broadcasts a query soliciting beacon replies for a (possibly empty) geohash.
// Inbound beacons/queries surface via callbacks. Transport only — the ERID rotation and geohash
// coarsening are the host's concern (this service never touches the stable UHID or precise location).
// Mirrors the green C# PresenceService.
//
// The payloads are encoded with snprintf (byte-identical to the C# System.Text.Json output — beacon
// field order erid, geohash, capabilities, status, sent_at_ms; query field order query_id, geohash;
// strings interpolated verbatim, bare-int capabilities/status/sent_at_ms, lowercase-dashed UUID) and
// decoded on receive with the vendored cJSON. Byte-identity gate: fixtures/presence/vectors.json.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex (matches sos.c / channels.c / prekey.c).

#ifndef AETHERNET_PRESENCE_H
#define AETHERNET_PRESENCE_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A PresenceBeacon (PacketType 21) payload. Mirrors the C# PresenceBeaconPayload. `erid` is the
 * node's current rotating Ephemeral Routing Id (Crockford base-32, NOT the UHID); `geohash` is the
 * coarse geohash ("" = hidden). Both are borrowed, null-terminated strings for the duration of any
 * call that consumes the beacon (the serializer copies what it retains). `capabilities` is a
 * NodeCapabilities bitmask; `status` a PresenceStatus value; `sent_at_ms` a Unix-ms timestamp.
 */
typedef struct {
    const char *erid;        // rotating ERID (Crockford base-32); never the UHID
    const char *geohash;     // coarse geohash; "" (or NULL) = hidden
    int32_t     capabilities; // NodeCapabilities bitmask
    int32_t     status;       // PresenceStatus value
    int64_t     sent_at_ms;   // Unix timestamp (ms) the beacon was sent
} aethernet_presence_beacon_t;

/**
 * Serialize a PresenceBeacon (PacketType 21) payload to canonical UTF-8 JSON:
 *   {"erid":"<erid>","geohash":"<geohash>","capabilities":<int>,"status":<int>,"sent_at_ms":<int64>}
 * Field order erid, geohash, capabilities, status, sent_at_ms; no whitespace; bare-int numbers.
 * Cross-language byte-identity gate (fixtures/presence/vectors.json). On success writes a
 * heap-allocated buffer to *out_json (null-terminated just past *out_len) and its length to *out_len,
 * returns true. Caller owns *out_json and frees with free(). Returns false on allocation failure or a
 * NULL required pointer. A NULL erid/geohash is treated as "".
 */
bool aethernet_presence_beacon_payload_serialize(const aethernet_presence_beacon_t *beacon,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len);

/**
 * Serialize a PresenceQuery (PacketType 22) payload to canonical UTF-8 JSON:
 *   {"query_id":"<uuid>","geohash":"<geohash>"}
 * Field order query_id, geohash; no whitespace; lowercase-dashed 36-char UUID. Cross-language
 * byte-identity gate (fixtures/presence/vectors.json). `query_id` is a 16-byte UUID; a NULL geohash is
 * treated as "". On success writes a heap-allocated buffer to *out_json (null-terminated just past
 * *out_len) and its length to *out_len, returns true. Caller owns *out_json and frees with free().
 * Returns false on allocation failure or a NULL required pointer.
 */
bool aethernet_presence_query_payload_serialize(const uint8_t query_id[AETHERNET_PACKET_ID_SIZE],
                                                const char *geohash,
                                                uint8_t **out_json,
                                                uint32_t *out_len);

/**
 * A received presence beacon surfaced to the host. Mirrors the C# PresenceBeaconReceived. Every field
 * is borrowed for the callback duration — copy anything you wish to retain. `beacon.erid`/
 * `beacon.geohash` are owned copies valid only for the call; `from_uhid` is the packet source.
 */
typedef struct {
    const aethernet_presence_beacon_t *beacon;    // borrowed; the decoded beacon
    const char                        *from_uhid; // borrowed; peer that sent the beacon
} aethernet_presence_beacon_received_t;

/**
 * A received presence query surfaced to the host. Mirrors the C# PresenceQueryReceived. `query_id` is
 * the echoed 16-byte query id; `geohash` an owned copy valid only for the call ("" = anywhere);
 * `from_uhid` the packet source. All borrowed for the callback duration.
 */
typedef struct {
    uint8_t     query_id[AETHERNET_PACKET_ID_SIZE]; // the query's id
    const char *geohash;                            // borrowed; queried geohash ("" = anywhere)
    const char *from_uhid;                          // borrowed; peer that sent the query
} aethernet_presence_query_received_t;

/** Beacon-received callback. Fired once per inbound PresenceBeacon. `event` borrowed for the call. */
typedef void (*aethernet_presence_beacon_received_cb)(const aethernet_presence_beacon_received_t *event,
                                                      void *user_data);

/** Query-received callback. Fired once per inbound PresenceQuery. `event` borrowed for the call. */
typedef void (*aethernet_presence_query_received_cb)(const aethernet_presence_query_received_t *event,
                                                     void *user_data);

/**
 * Opaque presence service handle. Broadcasts PresenceBeacon/PresenceQuery packets and surfaces inbound
 * beacons/queries via callbacks. The service borrows `sender` — caller keeps it alive for the service
 * lifetime.
 */
typedef struct aethernet_presence_service aethernet_presence_service_t;

aethernet_presence_service_t *aethernet_presence_service_new(aethernet_mesh_sender_t *sender);
void aethernet_presence_service_free(aethernet_presence_service_t *service);

/**
 * Broadcast a presence beacon (source local UHID, dest "*", TTL AETHERNET_DEFAULT_TTL) via
 * sender->broadcast. On success writes the fan-out count to *out_count (may be NULL to ignore) and
 * returns true. Returns false if `service`/`beacon` is NULL, the host wired no broadcast, or the
 * payload fails to encode. Mirrors the C# BroadcastBeaconAsync (returns peers reached).
 */
bool aethernet_presence_broadcast_beacon(aethernet_presence_service_t *service,
                                         const aethernet_presence_beacon_t *beacon,
                                         int *out_count);

/**
 * Broadcast a presence query for `geohash` (NULL/"" = anywhere): mint a fresh query id and broadcast a
 * PresenceQuery (dest "*", TTL AETHERNET_DEFAULT_TTL). On success writes the new 16-byte query id to
 * `out_query_id` (may be NULL to ignore), the fan-out count to *out_count (may be NULL), and returns
 * true. Returns false if `service` is NULL, the host wired no broadcast, or the payload fails to
 * encode. Mirrors the C# QueryAsync (returns the minted query id).
 */
bool aethernet_presence_query(aethernet_presence_service_t *service,
                              const char *geohash,
                              uint8_t out_query_id[AETHERNET_PACKET_ID_SIZE],
                              int *out_count);

/**
 * Process an inbound presence packet.
 *   - PresenceBeacon (21) with a non-empty erid → fire the beacon-received callback, return true.
 *   - PresenceQuery (22) → fire the query-received callback, return true.
 * Returns false for the wrong packet type, a malformed payload, a beacon with an empty erid, or a
 * NULL argument. Mirrors the C# HandleAsync.
 */
bool aethernet_presence_handle_packet(aethernet_presence_service_t *service,
                                      const aethernet_mesh_packet_t *packet);

/** Set the beacon-received callback (fired on each inbound PresenceBeacon). Pass NULL to clear. */
void aethernet_presence_set_beacon_received_cb(aethernet_presence_service_t *service,
                                               aethernet_presence_beacon_received_cb cb,
                                               void *user_data);

/** Set the query-received callback (fired on each inbound PresenceQuery). Pass NULL to clear. */
void aethernet_presence_set_query_received_cb(aethernet_presence_service_t *service,
                                              aethernet_presence_query_received_cb cb,
                                              void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PRESENCE_H
