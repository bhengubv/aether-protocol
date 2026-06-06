// SPDX-License-Identifier: MIT
// Aether DTN — store-and-forward bundles with custody transfer.

#ifndef AETHERNET_DTN_H
#define AETHERNET_DTN_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet_reputation.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    AETHERNET_BUNDLE_STATUS_PENDING    = 0,
    AETHERNET_BUNDLE_STATUS_IN_CUSTODY = 1,
    AETHERNET_BUNDLE_STATUS_DELIVERED  = 2,
    AETHERNET_BUNDLE_STATUS_EXPIRED    = 3,
    AETHERNET_BUNDLE_STATUS_FAILED     = 4,
} aethernet_bundle_status_t;

typedef enum {
    AETHERNET_BUNDLE_PRIORITY_LOW    = 0,
    AETHERNET_BUNDLE_PRIORITY_NORMAL = 1,
    AETHERNET_BUNDLE_PRIORITY_HIGH   = 2,
    AETHERNET_BUNDLE_PRIORITY_SOS    = 3,
} aethernet_bundle_priority_t;

/**
 * A delay-tolerant bundle. All char* / uint8_t* fields are owned by the bundle
 * and freed via aethernet_dtn_bundle_free().
 */
typedef struct {
    uint8_t  id[AETHERNET_PACKET_ID_SIZE]; // RFC 4122 UUID, 16 bytes
    char    *sender_uhid;               // owned
    char    *recipient_uhid;            // owned
    uint8_t *encrypted_payload;         // owned
    uint32_t encrypted_payload_len;
    uint8_t  priority;                  // aethernet_bundle_priority_t
    uint8_t  status;                    // aethernet_bundle_status_t
    int32_t  copy_count;
    int32_t  max_copies;
    char    *sender_geohash;            // owned, may be NULL
    char    *recipient_last_geohash;    // owned, may be NULL
    int32_t  hop_count;
    int64_t  created_at_ms;
    int64_t  expires_at_ms;
} aethernet_dtn_bundle_t;

/// Allocate a bundle (zero-initialized). Caller fills fields and frees via aethernet_dtn_bundle_free().
aethernet_dtn_bundle_t *aethernet_dtn_bundle_new(void);
void aethernet_dtn_bundle_free(aethernet_dtn_bundle_t *bundle);

/// True if the bundle has exceeded its expiry timestamp.
bool aethernet_dtn_bundle_is_expired(const aethernet_dtn_bundle_t *bundle);

/**
 * Opaque DTN service handle. Manages bundle store, custody records, and
 * delivery scan.
 */
typedef struct aethernet_dtn_service aethernet_dtn_service_t;

aethernet_dtn_service_t *aethernet_dtn_service_new(aethernet_mesh_sender_t *sender);
void aethernet_dtn_service_free(aethernet_dtn_service_t *service);

/**
 * Attach an optional reputation service. Pass NULL to detach.
 * When set, the DTN service fires reputation events:
 *   - delivery_success  when a bundle addressed to the local node is received.
 *   - custody_refusal   when a DTN custody-ack indicates the peer refused.
 * The caller retains ownership of `rep`; it must outlive the DTN service.
 */
void aethernet_dtn_set_reputation(aethernet_dtn_service_t *svc, AetherNetNodeReputationService *rep);

/**
 * Create a bundle and queue it for delivery. The bundle is saved to the local
 * store, an immediate direct-delivery attempt is made, and on failure the
 * bundle stays for the next delivery scan. Returns 0 on success, -1 on error.
 */
int aethernet_dtn_create_bundle(aethernet_dtn_service_t *service,
                             const char *recipient_uhid,
                             const uint8_t *encrypted_payload,
                             uint32_t encrypted_payload_len,
                             aethernet_bundle_priority_t priority,
                             const char *recipient_last_geohash);

/// Pump an inbound DTN-related packet (DtnBundle / DtnCustodyAck / DtnDeliveryReceipt).
void aethernet_dtn_handle_packet(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet);

/// Run one delivery scan pass: re-attempt direct delivery + replicate via the strategy.
void aethernet_dtn_run_delivery_scan(aethernet_dtn_service_t *service);

/// Mark every expired bundle as Expired in the store. Returns the count affected.
int aethernet_dtn_expire_stale(aethernet_dtn_service_t *service);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_DTN_H
