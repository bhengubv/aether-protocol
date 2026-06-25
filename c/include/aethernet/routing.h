// SPDX-License-Identifier: MIT
// Aether mesh routing — AODV-inspired RREQ/RREP discovery.

#ifndef AETHERNET_ROUTING_H
#define AETHERNET_ROUTING_H

#include <stdbool.h>
#include <stdint.h>
#include <time.h>

#include "aethernet/protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Forward declaration — include aethernet_reputation.h if you need the full type. */
typedef struct AetherNetNodeReputationService AetherNetNodeReputationService;

/**
 * A single entry in the routing table.
 */
typedef struct {
    char *destination_uhid;     // owned, null-terminated
    char *next_hop_uhid;        // owned, null-terminated
    int32_t hop_count;
    int32_t quality_score;      // 0..100
    int64_t expires_at_ms;      // unix epoch ms
} aethernet_route_entry_t;

/**
 * A connected peer as seen by the DTN epidemic-replication strategy
 * (GeohashEpidemicStrategy). Every field is borrowed for the duration of the
 * connected_peers() call only — the DTN layer copies whatever it retains.
 * `geohash` may be NULL (peer has not shared a location).
 */
typedef struct {
    const char *uhid;              // peer UHID, null-terminated, never NULL
    const char *geohash;           // peer's last-known geohash, or NULL
    uint32_t    capabilities;      // node-capability bitfield; DTN carrier = AETHERNET_CAP_DTN_CARRIER (0x80)
    int32_t     reliability_score; // 0..100, higher = more reliable
    bool        is_blocked;        // true if the host has blocked this peer
} aethernet_peer_info_t;

/**
 * MeshSender callback hooks. Hosts implement these and pass them to the routing
 * service so the routing layer can forward packets without taking a hard
 * dependency on a specific transport.
 */
typedef struct aethernet_mesh_sender {
    /// Local node's UHID (null-terminated). Borrowed by the routing service.
    const char *local_uhid;
    /// Local geohash, or NULL if not shared.
    const char *local_geohash;

    /// Forward `packet` to a single next-hop peer. Returns true if delivered.
    bool (*send)(struct aethernet_mesh_sender *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid);

    /// Broadcast `packet` to every connected peer. Returns the fan-out count.
    int (*broadcast)(struct aethernet_mesh_sender *self, const aethernet_mesh_packet_t *packet);

    /// Opaque host-side state.
    void *user_data;

    /**
     * Enumerate currently-connected peers for DTN epidemic replication. Writes up
     * to `max` entries into `out_peers` and returns the count written (0..max), or
     * a negative value on error. MAY BE NULL — DTN then falls back to direct
     * delivery only (single-copy, no multi-copy epidemic replication). The
     * borrowed string fields in each entry must stay valid until the call returns.
     * Added for DTN GeohashEpidemicStrategy parity (Wave 19).
     */
    int (*connected_peers)(struct aethernet_mesh_sender *self, aethernet_peer_info_t *out_peers, int max);
} aethernet_mesh_sender_t;

/**
 * Opaque routing service handle. Manages a route cache, RREQ deduplication,
 * pending discovery requests, and serializes mutation behind an internal lock.
 */
typedef struct aethernet_routing_service aethernet_routing_service_t;

/**
 * Create a routing service backed by an in-memory store. The service borrows
 * `sender` — caller must keep it alive for the service lifetime.
 */
aethernet_routing_service_t *aethernet_routing_service_new(aethernet_mesh_sender_t *sender);

/**
 * Destroy the routing service and its internal state.
 */
void aethernet_routing_service_free(aethernet_routing_service_t *service);

/**
 * Find a route to `destination_uhid`. Returns true and fills `out_route`
 * with a heap-allocated copy on success (caller frees via aethernet_route_entry_free).
 * Returns false if no route exists and discovery would be required (this
 * synchronous API does not block on RREP — hosts call from a worker that can
 * tolerate a discovery cycle).
 */
bool aethernet_routing_find_cached(aethernet_routing_service_t *service,
                                const char *destination_uhid,
                                aethernet_route_entry_t **out_route);

/**
 * Trigger a route discovery for `destination_uhid` if no fresh cached route
 * exists. Sends RREQ to all peers via the sender's broadcast. Returns 0 on
 * success, -1 on error, 1 if a fresh cached route already existed.
 */
int aethernet_routing_discover(aethernet_routing_service_t *service, const char *destination_uhid);

/**
 * Process an inbound RREQ packet — install a reverse route and either reply
 * (we are the destination), reply on behalf (we know the destination), or
 * forward (decrementing TTL).
 */
void aethernet_routing_handle_rreq(aethernet_routing_service_t *service, aethernet_mesh_packet_t *rreq);

/**
 * Process an inbound RREP packet — install the forward route and complete any
 * pending discovery, otherwise forward toward the original requester.
 */
void aethernet_routing_handle_rrep(aethernet_routing_service_t *service, aethernet_mesh_packet_t *rrep);

/**
 * Prune expired routes and trim the RREQ deduplication state. Returns the
 * number of routes evicted.
 */
int aethernet_routing_prune(aethernet_routing_service_t *service);

/**
 * Free a route entry returned by aethernet_routing_find_cached().
 */
void aethernet_route_entry_free(aethernet_route_entry_t *route);

/**
 * Attach an optional reputation service. Pass NULL to disable.
 * When set, RREQ flood attempts are recorded against the source UHID.
 */
void aethernet_routing_set_reputation(aethernet_routing_service_t *service,
                                   AetherNetNodeReputationService *reputation);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_ROUTING_H
