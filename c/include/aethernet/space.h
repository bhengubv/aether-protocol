// SPDX-License-Identifier: MIT
// aether-space: geo-pinned community noticeboards (Phase-2 extension).
//
// Nodes drop breadcrumbs at geohash coordinates; passing devices auto-pull and
// re-host them for other passersby — fully offline. Port of the C# reference
// (AetherNet.Space). Wire format: JSON, PacketType SpaceBreadcrumb (40).

#ifndef AETHERNET_SPACE_H
#define AETHERNET_SPACE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    AETHERNET_BREADCRUMB_NOTICE      = 0,
    AETHERNET_BREADCRUMB_EMERGENCY   = 1,
    AETHERNET_BREADCRUMB_COMMERCE    = 2,
    AETHERNET_BREADCRUMB_EVENT       = 3,
    AETHERNET_BREADCRUMB_JOB_POSTING = 4,
} aethernet_breadcrumb_type_t;

#define AETHERNET_SPACE_EMERGENCY_TTL_HOURS 720
#define AETHERNET_SPACE_MIN_TTL_HOURS         1
#define AETHERNET_SPACE_MAX_TTL_HOURS       168

/**
 * A geo-pinned digital notice. All owned pointer (char* / uint8_t*) fields are freed by the
 * breadcrumb and freed by aethernet_space_breadcrumb_free().
 */
typedef struct {
    char    *content_hash;  // owned
    char    *geo_hash;      // owned
    char    *anchor_uhid;   // owned
    int64_t  created_at_ms;
    int32_t  ttl_hours;
    uint8_t  type;          // aethernet_breadcrumb_type_t
    uint8_t *signature;     // owned, may be NULL
    uint32_t signature_len;
} aethernet_space_breadcrumb_t;

void aethernet_space_breadcrumb_free(aethernet_space_breadcrumb_t *crumb);

/// Unix-epoch ms of expiry = created_at_ms + ttl_hours.
int64_t aethernet_space_breadcrumb_expires_at_ms(const aethernet_space_breadcrumb_t *crumb);
/// True once the breadcrumb's TTL has passed.
bool aethernet_space_breadcrumb_is_expired(const aethernet_space_breadcrumb_t *crumb);

/// Opaque in-memory space service handle.
typedef struct aethernet_space_service aethernet_space_service_t;

aethernet_space_service_t *aethernet_space_service_new(void);
void aethernet_space_service_free(aethernet_space_service_t *service);

/// Callback fired when a breadcrumb is dropped locally / pinned, or expired.
/// The pointer is valid only for the duration of the callback.
typedef void (*aethernet_space_breadcrumb_cb)(const aethernet_space_breadcrumb_t *crumb, void *user_data);
void aethernet_space_set_received_callback(aethernet_space_service_t *service, aethernet_space_breadcrumb_cb cb, void *user_data);
void aethernet_space_set_expired_callback(aethernet_space_service_t *service, aethernet_space_breadcrumb_cb cb, void *user_data);

/**
 * Drop a new breadcrumb at geo_hash. ttl_hours is clamped to [1,168]; Emergency
 * breadcrumbs are fixed at 720 h. Returns a borrowed pointer to the stored
 * breadcrumb (owned by the service), or NULL on allocation failure.
 */
const aethernet_space_breadcrumb_t *aethernet_space_drop(
    aethernet_space_service_t *service,
    const char *geo_hash, const char *content_hash, const char *anchor_uhid,
    aethernet_breadcrumb_type_t type, int32_t ttl_hours);

/**
 * Scan for active (non-expired) breadcrumbs near center_geo_hash. Writes up to
 * `max` borrowed breadcrumb pointers into out_results; returns the count
 * written (>=0). Pointers are owned by the service and valid until the next
 * mutating call.
 */
int aethernet_space_scan(
    aethernet_space_service_t *service,
    const char *center_geo_hash, int32_t radius_cells,
    const aethernet_space_breadcrumb_t **out_results, int max);

/// Cache and re-host a breadcrumb received from a peer (takes ownership of *crumb's fields by copy).
void aethernet_space_pin(aethernet_space_service_t *service, const aethernet_space_breadcrumb_t *crumb);

/// Creator-only delete: succeeds only if requestor_uhid is the breadcrumb's anchor_uhid.
bool aethernet_space_delete(aethernet_space_service_t *service, const char *content_hash, const char *requestor_uhid);

/// Drop every expired breadcrumb; returns the count removed.
int aethernet_space_prune_expired(aethernet_space_service_t *service);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_SPACE_H
