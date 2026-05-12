// SPDX-License-Identifier: MIT
// Aether DTN — store-and-forward bundles with custody transfer.

#ifndef AETHER_DTN_H
#define AETHER_DTN_H

#include <stdbool.h>
#include <stdint.h>

#include "aether/protocol.h"
#include "aether/routing.h"
#include "aether_reputation.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    AETHER_BUNDLE_STATUS_PENDING    = 0,
    AETHER_BUNDLE_STATUS_IN_CUSTODY = 1,
    AETHER_BUNDLE_STATUS_DELIVERED  = 2,
    AETHER_BUNDLE_STATUS_EXPIRED    = 3,
    AETHER_BUNDLE_STATUS_FAILED     = 4,
} aether_bundle_status_t;

typedef enum {
    AETHER_BUNDLE_PRIORITY_LOW    = 0,
    AETHER_BUNDLE_PRIORITY_NORMAL = 1,
    AETHER_BUNDLE_PRIORITY_HIGH   = 2,
    AETHER_BUNDLE_PRIORITY_SOS    = 3,
} aether_bundle_priority_t;

/**
 * A delay-tolerant bundle. All char* / uint8_t* fields are owned by the bundle
 * and freed via aether_dtn_bundle_free().
 */
typedef struct {
    uint8_t  id[AETHER_PACKET_ID_SIZE]; // RFC 4122 UUID, 16 bytes
    char    *sender_uhid;               // owned
    char    *recipient_uhid;            // owned
    uint8_t *encrypted_payload;         // owned
    uint32_t encrypted_payload_len;
    uint8_t  priority;                  // aether_bundle_priority_t
    uint8_t  status;                    // aether_bundle_status_t
    int32_t  copy_count;
    int32_t  max_copies;
    char    *sender_geohash;            // owned, may be NULL
    char    *recipient_last_geohash;    // owned, may be NULL
    int32_t  hop_count;
    int64_t  created_at_ms;
    int64_t  expires_at_ms;
} aether_dtn_bundle_t;

/// Allocate a bundle (zero-initialized). Caller fills fields and frees via aether_dtn_bundle_free().
aether_dtn_bundle_t *aether_dtn_bundle_new(void);
void aether_dtn_bundle_free(aether_dtn_bundle_t *bundle);

/// True if the bundle has exceeded its expiry timestamp.
bool aether_dtn_bundle_is_expired(const aether_dtn_bundle_t *bundle);

/**
 * Opaque DTN service handle. Manages bundle store, custody records, and
 * delivery scan.
 */
typedef struct aether_dtn_service aether_dtn_service_t;

aether_dtn_service_t *aether_dtn_service_new(aether_mesh_sender_t *sender);
void aether_dtn_service_free(aether_dtn_service_t *service);

/**
 * Attach an optional reputation service. Pass NULL to detach.
 * When set, the DTN service fires reputation events:
 *   - delivery_success  when a bundle addressed to the local node is received.
 *   - custody_refusal   when a DTN custody-ack indicates the peer refused.
 * The caller retains ownership of `rep`; it must outlive the DTN service.
 */
void aether_dtn_set_reputation(aether_dtn_service_t *svc, AetherNodeReputationService *rep);

/**
 * Create a bundle and queue it for delivery. The bundle is saved to the local
 * store, an immediate direct-delivery attempt is made, and on failure the
 * bundle stays for the next delivery scan. Returns 0 on success, -1 on error.
 */
int aether_dtn_create_bundle(aether_dtn_service_t *service,
                             const char *recipient_uhid,
                             const uint8_t *encrypted_payload,
                             uint32_t encrypted_payload_len,
                             aether_bundle_priority_t priority,
                             const char *recipient_last_geohash);

/// Pump an inbound DTN-related packet (DtnBundle / DtnCustodyAck / DtnDeliveryReceipt).
void aether_dtn_handle_packet(aether_dtn_service_t *service, const aether_mesh_packet_t *packet);

/// Run one delivery scan pass: re-attempt direct delivery + replicate via the strategy.
void aether_dtn_run_delivery_scan(aether_dtn_service_t *service);

/// Mark every expired bundle as Expired in the store. Returns the count affected.
int aether_dtn_expire_stale(aether_dtn_service_t *service);

#ifdef __cplusplus
}
#endif

#endif // AETHER_DTN_H
