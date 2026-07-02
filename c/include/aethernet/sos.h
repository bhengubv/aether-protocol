// SPDX-License-Identifier: MIT
// Aether SOS — flood-broadcast emergency alerts with rate limiting.

#ifndef AETHERNET_SOS_H
#define AETHERNET_SOS_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * An SOS alert observed on the mesh — locally originated or received.
 */
typedef struct {
    uint8_t  id[AETHERNET_PACKET_ID_SIZE]; // 16-byte UUID
    char    *sender_uhid;               // owned
    char    *broadcast_type;            // owned ("sos", "panic", "medical", ...)
    char    *message;                   // owned, may be NULL
    double   latitude;
    double   longitude;
    char    *geohash;                   // owned, may be NULL
    int64_t  received_at_ms;

    /**
     * Distinct UHIDs of peers that have acknowledged receiving this alert. Populated on the
     * ORIGINATING node only, as SosAck packets arrive back — it lets the sender see how many
     * devices their emergency reached. Owned by the alert; each entry is a heap-allocated,
     * null-terminated string. Do not mutate directly; the SOS service maintains the set.
     */
    char   **acknowledged_by;           // owned array of owned strings, may be NULL
    int      acknowledged_by_count;
} aethernet_sos_alert_t;

aethernet_sos_alert_t *aethernet_sos_alert_new(void);
void aethernet_sos_alert_free(aethernet_sos_alert_t *alert);

/**
 * Serialize a SosAck payload (PacketType 6) to canonical UTF-8 JSON:
 *   {"broadcast_id":"<uuid>","received_at_ms":<int>}
 * snake_case keys, field order broadcast_id then received_at_ms, no whitespace, lowercase-dashed
 * 36-char UUID, received_at_ms a bare integer. This is the cross-language byte-identity gate
 * (fixtures/sos/vectors.json) — every SDK must emit exactly these bytes.
 *
 * `broadcast_id` is a 16-byte UUID. On success, writes a heap-allocated buffer to *out_json (NOT
 * null-terminated beyond *out_len; the caller may treat [0, *out_len) as the JSON bytes) and its
 * length to *out_len, and returns true. The caller owns *out_json and frees it with free().
 * Returns false on allocation failure.
 */
bool aethernet_sos_ack_payload_serialize(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                      int64_t received_at_ms,
                                      uint8_t **out_json,
                                      uint32_t *out_len);

/**
 * Opaque SOS service handle. Manages dedup state, active alerts, and rate limiting.
 */
typedef struct aethernet_sos_service aethernet_sos_service_t;

aethernet_sos_service_t *aethernet_sos_service_new(aethernet_mesh_sender_t *sender);
void aethernet_sos_service_free(aethernet_sos_service_t *service);

/**
 * Originate an SOS broadcast. Floods the mesh via sender->broadcast.
 * Returns 0 on success, 1 if rate-limited (>= MAX_SOS_BROADCASTS_PER_HOUR
 * originations in the last rolling hour), -1 on error.
 */
int aethernet_sos_broadcast(aethernet_sos_service_t *service,
                         const char *broadcast_type,
                         const char *message,
                         double latitude,
                         double longitude,
                         const char *geohash);

/**
 * Pump an inbound SOS packet. Dedups by packet id, surfaces the alert via the
 * registered callback (if set), and re-broadcasts if TTL allows.
 *
 * `packet` is mutated in place (TTL decrement) — callers should not reuse it.
 */
void aethernet_sos_handle_packet(aethernet_sos_service_t *service, aethernet_mesh_packet_t *packet);

/**
 * Pump an inbound SosAck packet (PacketType 6) into the service. On the ORIGINATING node it
 * parses the ack payload (`broadcast_id`, `received_at_ms`), finds the matching active alert
 * this node originated, records the responder (the ack's source UHID) — deduping by responder —
 * and fires the acknowledged callback with the running distinct count. No-op if the ack
 * references an SOS this node did not originate (or already resolved), if the responder is the
 * local node (our own ack echoed back), or if the responder is already counted.
 *
 * Returns 0 on success (including the benign no-op cases above), -1 if `service`/`packet` is
 * NULL or the packet is not a SosAck.
 */
int aethernet_sos_handle_ack(aethernet_sos_service_t *service, const aethernet_mesh_packet_t *packet);

/**
 * Mark an SOS resolved locally; future inbound copies of the same packet id are
 * still deduplicated, but the alert is removed from the active set.
 * `broadcast_id` is a 16-byte UUID.
 */
void aethernet_sos_resolve(aethernet_sos_service_t *service, const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE]);

/**
 * Receive callback. Invoked once per new alert. The alert is owned by the
 * service; the callback must copy any fields it wants to retain.
 */
typedef void (*aethernet_sos_received_cb)(const aethernet_sos_alert_t *alert, void *user_data);

void aethernet_sos_set_received_cb(aethernet_sos_service_t *service,
                                aethernet_sos_received_cb cb,
                                void *user_data);

/**
 * Acknowledged callback. Fired on the ORIGINATING node once per NEW distinct responder that
 * acknowledges one of our active SOS alerts. `broadcast_id` is the 16-byte UUID of the alert;
 * `responder_uhid` is the acknowledging peer (null-terminated); `total` is the running count of
 * distinct responders (this one included). All arguments are borrowed for the callback duration.
 */
typedef void (*aethernet_sos_acknowledged_cb)(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                            const char *responder_uhid,
                                            int total,
                                            void *user_data);

void aethernet_sos_set_acknowledged_cb(aethernet_sos_service_t *service,
                                    aethernet_sos_acknowledged_cb cb,
                                    void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_SOS_H
