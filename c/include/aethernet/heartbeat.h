// SPDX-License-Identifier: MIT
// Aether Heartbeat — periodic single-hop liveness beacons (PacketType 10).

#ifndef AETHERNET_HEARTBEAT_H
#define AETHERNET_HEARTBEAT_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A peer's last observed liveness, maintained on the receiving node from the heartbeats a peer
 * broadcasts. Mirrors the C# PeerLiveness record. `uhid` is owned by the heartbeat service (the
 * enclosing packet's source UHID); callbacks borrow it for their duration and must copy to retain.
 */
typedef struct {
    char   *uhid;            // owned; UHID of the peer this record describes
    int32_t last_sequence;   // Sequence of the most recent heartbeat seen from the peer
    int64_t last_sent_at_ms; // peer-stamped SentAtMs of the most recent heartbeat
    int64_t received_at_ms;  // local Unix-ms timestamp when the most recent heartbeat was received
} aethernet_peer_liveness_t;

/**
 * Serialize a HeartbeatPayload (PacketType 10) to canonical UTF-8 JSON:
 *   {"sequence":<int>,"sent_at_ms":<int>}
 * snake_case keys, field order sequence then sent_at_ms, no whitespace, both values bare integers.
 * This is the cross-language byte-identity gate (fixtures/heartbeat/vectors.json) — every SDK must
 * emit exactly these bytes.
 *
 * On success, writes a heap-allocated buffer to *out_json (null-terminated just past *out_len; the
 * caller may treat [0, *out_len) as the JSON bytes) and its length to *out_len, and returns true.
 * The caller owns *out_json and frees it with free(). Returns false on allocation failure or if
 * out_json / out_len is NULL.
 */
bool aethernet_heartbeat_payload_serialize(int32_t sequence,
                                           int64_t sent_at_ms,
                                           uint8_t **out_json,
                                           uint32_t *out_len);

/**
 * Opaque heartbeat service handle. Tracks the outgoing sequence counter and a per-peer liveness
 * table keyed by source UHID. The service borrows `sender` — caller keeps it alive for the service
 * lifetime.
 *
 * Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
 * in their own mutex (matches sos.c).
 */
typedef struct aethernet_heartbeat_service aethernet_heartbeat_service_t;

aethernet_heartbeat_service_t *aethernet_heartbeat_service_new(aethernet_mesh_sender_t *sender);
void aethernet_heartbeat_service_free(aethernet_heartbeat_service_t *service);

/**
 * Broadcast a single heartbeat to all directly connected peers (source = local, dest = "*", TTL 1
 * — liveness of DIRECT neighbours only). The sequence number increments on every call (starts at
 * 1). Returns the number of peers the beacon was delivered to (the sender's broadcast fan-out), or
 * -1 if `service` is NULL.
 */
int aethernet_heartbeat_send(aethernet_heartbeat_service_t *service);

/**
 * Process an inbound Heartbeat packet: refresh the sender's liveness record (keyed by source UHID)
 * and fire the peer-seen callback. Returns false (no-op) for the wrong packet type, a
 * self-originated heartbeat echoed back, a NULL argument, or a malformed payload; otherwise records
 * the peer and returns true. Heartbeats are single-hop — the receiver never re-broadcasts.
 */
bool aethernet_heartbeat_handle_packet(aethernet_heartbeat_service_t *service,
                                       const aethernet_mesh_packet_t *packet);

/**
 * Snapshot of every peer this node has ever seen a heartbeat from. Writes a heap-allocated array of
 * `aethernet_peer_liveness_t` (deep copies, each with its own owned `uhid`) to *out_peers and the
 * count to *out_count, returning the count (0 on empty, -1 on error). The caller owns the array and
 * frees it with aethernet_peer_liveness_list_free().
 */
int aethernet_heartbeat_get_known_peers(const aethernet_heartbeat_service_t *service,
                                        aethernet_peer_liveness_t **out_peers,
                                        int *out_count);

/**
 * Peers whose most recent heartbeat was received within the last `within_seconds` seconds (relative
 * to now). Same ownership contract as aethernet_heartbeat_get_known_peers. A negative
 * `within_seconds` pushes the recency horizon into the future and excludes even a just-seen peer.
 */
int aethernet_heartbeat_get_live_peers(const aethernet_heartbeat_service_t *service,
                                       int within_seconds,
                                       aethernet_peer_liveness_t **out_peers,
                                       int *out_count);

/**
 * Free an array returned by aethernet_heartbeat_get_known_peers / _get_live_peers, including each
 * entry's owned `uhid`. Safe to call with NULL / count 0.
 */
void aethernet_peer_liveness_list_free(aethernet_peer_liveness_t *peers, int count);

/**
 * Peer-seen callback. Fired once per received heartbeat (new or refreshed liveness). `liveness` is
 * borrowed for the callback duration — copy any fields to retain. Mirrors the C# PeerSeen event.
 */
typedef void (*aethernet_heartbeat_peer_seen_cb)(const aethernet_peer_liveness_t *liveness,
                                                 void *user_data);

void aethernet_heartbeat_set_peer_seen_cb(aethernet_heartbeat_service_t *service,
                                          aethernet_heartbeat_peer_seen_cb cb,
                                          void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_HEARTBEAT_H
