// SPDX-License-Identifier: MIT
// Aether DTN — store-and-forward bundles with custody transfer.

#ifndef AETHERMESH_DTN_H
#define AETHERMESH_DTN_H

#include <stdbool.h>
#include <stdint.h>

#include "aethermesh/protocol.h"
#include "aethermesh/routing.h"
#include "aethermesh_reputation.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    AETHERMESH_BUNDLE_STATUS_PENDING    = 0,
    AETHERMESH_BUNDLE_STATUS_IN_CUSTODY = 1,
    AETHERMESH_BUNDLE_STATUS_DELIVERED  = 2,
    AETHERMESH_BUNDLE_STATUS_EXPIRED    = 3,
    AETHERMESH_BUNDLE_STATUS_FAILED     = 4,
} aethermesh_bundle_status_t;

typedef enum {
    AETHERMESH_BUNDLE_PRIORITY_LOW    = 0,
    AETHERMESH_BUNDLE_PRIORITY_NORMAL = 1,
    AETHERMESH_BUNDLE_PRIORITY_HIGH   = 2,
    AETHERMESH_BUNDLE_PRIORITY_SOS    = 3,
} aethermesh_bundle_priority_t;

/**
 * A delay-tolerant bundle. All char* / uint8_t* fields are owned by the bundle
 * and freed via aethermesh_dtn_bundle_free().
 */
typedef struct {
    uint8_t  id[AETHERMESH_PACKET_ID_SIZE]; // RFC 4122 UUID, 16 bytes
    char    *sender_uhid;               // owned
    char    *recipient_uhid;            // owned
    uint8_t *encrypted_payload;         // owned
    uint32_t encrypted_payload_len;
    uint8_t  priority;                  // aethermesh_bundle_priority_t
    uint8_t  status;                    // aethermesh_bundle_status_t
    int32_t  copy_count;
    int32_t  max_copies;
    char    *sender_geohash;            // owned, may be NULL
    char    *recipient_last_geohash;    // owned, may be NULL
    int32_t  hop_count;
    int64_t  created_at_ms;
    int64_t  expires_at_ms;
} aethermesh_dtn_bundle_t;

/// Allocate a bundle (zero-initialized). Caller fills fields and frees via aethermesh_dtn_bundle_free().
aethermesh_dtn_bundle_t *aethermesh_dtn_bundle_new(void);
void aethermesh_dtn_bundle_free(aethermesh_dtn_bundle_t *bundle);

/// True if the bundle has exceeded its expiry timestamp.
bool aethermesh_dtn_bundle_is_expired(const aethermesh_dtn_bundle_t *bundle);

/**
 * Opaque DTN service handle. Manages bundle store, custody records, and
 * delivery scan.
 */
typedef struct aethermesh_dtn_service aethermesh_dtn_service_t;

aethermesh_dtn_service_t *aethermesh_dtn_service_new(aethermesh_mesh_sender_t *sender);
void aethermesh_dtn_service_free(aethermesh_dtn_service_t *service);

/**
 * Attach an optional reputation service. Pass NULL to detach.
 * When set, the DTN service fires reputation events:
 *   - delivery_success  when a bundle addressed to the local node is received.
 *   - custody_refusal   when a DTN custody-ack indicates the peer refused.
 * The caller retains ownership of `rep`; it must outlive the DTN service.
 */
void aethermesh_dtn_set_reputation(aethermesh_dtn_service_t *svc, AetherMeshNodeReputationService *rep);

/**
 * Create a bundle and queue it for delivery. The bundle is saved to the local
 * store, an immediate direct-delivery attempt is made, and on failure the
 * bundle stays for the next delivery scan. Returns 0 on success, -1 on error.
 */
int aethermesh_dtn_create_bundle(aethermesh_dtn_service_t *service,
                             const char *recipient_uhid,
                             const uint8_t *encrypted_payload,
                             uint32_t encrypted_payload_len,
                             aethermesh_bundle_priority_t priority,
                             const char *recipient_last_geohash);

/// Pump an inbound DTN-related packet (DtnBundle / DtnCustodyAck / DtnDeliveryReceipt).
void aethermesh_dtn_handle_packet(aethermesh_dtn_service_t *service, const aethermesh_mesh_packet_t *packet);

/// Run one delivery scan pass: re-attempt direct delivery + replicate via the strategy.
void aethermesh_dtn_run_delivery_scan(aethermesh_dtn_service_t *service);

/// Mark every expired bundle as Expired in the store. Returns the count affected.
int aethermesh_dtn_expire_stale(aethermesh_dtn_service_t *service);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_DTN_H
