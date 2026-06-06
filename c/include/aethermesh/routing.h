// SPDX-License-Identifier: MIT
// Aether mesh routing — AODV-inspired RREQ/RREP discovery.

#ifndef AETHERMESH_ROUTING_H
#define AETHERMESH_ROUTING_H

#include <stdbool.h>
#include <stdint.h>
#include <time.h>

#include "aethermesh/protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Forward declaration — include aethermesh_reputation.h if you need the full type. */
typedef struct AetherMeshNodeReputationService AetherMeshNodeReputationService;

/**
 * A single entry in the routing table.
 */
typedef struct {
    char *destination_uhid;     // owned, null-terminated
    char *next_hop_uhid;        // owned, null-terminated
    int32_t hop_count;
    int32_t quality_score;      // 0..100
    int64_t expires_at_ms;      // unix epoch ms
} aethermesh_route_entry_t;

/**
 * MeshSender callback hooks. Hosts implement these and pass them to the routing
 * service so the routing layer can forward packets without taking a hard
 * dependency on a specific transport.
 */
typedef struct aethermesh_mesh_sender {
    /// Local node's UHID (null-terminated). Borrowed by the routing service.
    const char *local_uhid;
    /// Local geohash, or NULL if not shared.
    const char *local_geohash;

    /// Forward `packet` to a single next-hop peer. Returns true if delivered.
    bool (*send)(struct aethermesh_mesh_sender *self, const aethermesh_mesh_packet_t *packet, const char *next_hop_uhid);

    /// Broadcast `packet` to every connected peer. Returns the fan-out count.
    int (*broadcast)(struct aethermesh_mesh_sender *self, const aethermesh_mesh_packet_t *packet);

    /// Opaque host-side state.
    void *user_data;
} aethermesh_mesh_sender_t;

/**
 * Opaque routing service handle. Manages a route cache, RREQ deduplication,
 * pending discovery requests, and serializes mutation behind an internal lock.
 */
typedef struct aethermesh_routing_service aethermesh_routing_service_t;

/**
 * Create a routing service backed by an in-memory store. The service borrows
 * `sender` — caller must keep it alive for the service lifetime.
 */
aethermesh_routing_service_t *aethermesh_routing_service_new(aethermesh_mesh_sender_t *sender);

/**
 * Destroy the routing service and its internal state.
 */
void aethermesh_routing_service_free(aethermesh_routing_service_t *service);

/**
 * Find a route to `destination_uhid`. Returns true and fills `out_route`
 * with a heap-allocated copy on success (caller frees via aethermesh_route_entry_free).
 * Returns false if no route exists and discovery would be required (this
 * synchronous API does not block on RREP — hosts call from a worker that can
 * tolerate a discovery cycle).
 */
bool aethermesh_routing_find_cached(aethermesh_routing_service_t *service,
                                const char *destination_uhid,
                                aethermesh_route_entry_t **out_route);

/**
 * Trigger a route discovery for `destination_uhid` if no fresh cached route
 * exists. Sends RREQ to all peers via the sender's broadcast. Returns 0 on
 * success, -1 on error, 1 if a fresh cached route already existed.
 */
int aethermesh_routing_discover(aethermesh_routing_service_t *service, const char *destination_uhid);

/**
 * Process an inbound RREQ packet — install a reverse route and either reply
 * (we are the destination), reply on behalf (we know the destination), or
 * forward (decrementing TTL).
 */
void aethermesh_routing_handle_rreq(aethermesh_routing_service_t *service, aethermesh_mesh_packet_t *rreq);

/**
 * Process an inbound RREP packet — install the forward route and complete any
 * pending discovery, otherwise forward toward the original requester.
 */
void aethermesh_routing_handle_rrep(aethermesh_routing_service_t *service, aethermesh_mesh_packet_t *rrep);

/**
 * Prune expired routes and trim the RREQ deduplication state. Returns the
 * number of routes evicted.
 */
int aethermesh_routing_prune(aethermesh_routing_service_t *service);

/**
 * Free a route entry returned by aethermesh_routing_find_cached().
 */
void aethermesh_route_entry_free(aethermesh_route_entry_t *route);

/**
 * Attach an optional reputation service. Pass NULL to disable.
 * When set, RREQ flood attempts are recorded against the source UHID.
 */
void aethermesh_routing_set_reputation(aethermesh_routing_service_t *service,
                                   AetherMeshNodeReputationService *reputation);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_ROUTING_H
