// SPDX-License-Identifier: MIT
// Aether SOS — flood-broadcast emergency alerts with rate limiting.

#ifndef AETHERMESH_SOS_H
#define AETHERMESH_SOS_H

#include <stdbool.h>
#include <stdint.h>

#include "aethermesh/protocol.h"
#include "aethermesh/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * An SOS alert observed on the mesh — locally originated or received.
 */
typedef struct {
    uint8_t  id[AETHERMESH_PACKET_ID_SIZE]; // 16-byte UUID
    char    *sender_uhid;               // owned
    char    *broadcast_type;            // owned ("sos", "panic", "medical", ...)
    char    *message;                   // owned, may be NULL
    double   latitude;
    double   longitude;
    char    *geohash;                   // owned, may be NULL
    int64_t  received_at_ms;
} aethermesh_sos_alert_t;

aethermesh_sos_alert_t *aethermesh_sos_alert_new(void);
void aethermesh_sos_alert_free(aethermesh_sos_alert_t *alert);

/**
 * Opaque SOS service handle. Manages dedup state, active alerts, and rate limiting.
 */
typedef struct aethermesh_sos_service aethermesh_sos_service_t;

aethermesh_sos_service_t *aethermesh_sos_service_new(aethermesh_mesh_sender_t *sender);
void aethermesh_sos_service_free(aethermesh_sos_service_t *service);

/**
 * Originate an SOS broadcast. Floods the mesh via sender->broadcast.
 * Returns 0 on success, 1 if rate-limited (>= MAX_SOS_BROADCASTS_PER_HOUR
 * originations in the last rolling hour), -1 on error.
 */
int aethermesh_sos_broadcast(aethermesh_sos_service_t *service,
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
void aethermesh_sos_handle_packet(aethermesh_sos_service_t *service, aethermesh_mesh_packet_t *packet);

/**
 * Mark an SOS resolved locally; future inbound copies of the same packet id are
 * still deduplicated, but the alert is removed from the active set.
 * `broadcast_id` is a 16-byte UUID.
 */
void aethermesh_sos_resolve(aethermesh_sos_service_t *service, const uint8_t broadcast_id[AETHERMESH_PACKET_ID_SIZE]);

/**
 * Receive callback. Invoked once per new alert. The alert is owned by the
 * service; the callback must copy any fields it wants to retain.
 */
typedef void (*aethermesh_sos_received_cb)(const aethermesh_sos_alert_t *alert, void *user_data);

void aethermesh_sos_set_received_cb(aethermesh_sos_service_t *service,
                                aethermesh_sos_received_cb cb,
                                void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_SOS_H
