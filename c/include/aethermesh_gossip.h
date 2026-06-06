// SPDX-License-Identifier: MIT
// Aether ReputationGossipService — signed reputation-update propagation.
//
// Nodes broadcast signed AetherMeshReputationUpdatePayload packets (type 52)
// to their peers.  Inbound packets are verified, freshness-checked, and
// applied to the local reputation store with a weight proportional to the
// reporter's own reputation score.
//
// NOTE: Not thread-safe by design — single-threaded embedded targets only.

#ifndef AETHERMESH_GOSSIP_H
#define AETHERMESH_GOSSIP_H

#include "aethermesh_reputation.h"
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Packet type identifier for reputation-update gossip packets. */
#define AETHERMESH_PACKET_TYPE_REPUTATION_UPDATE 52

/** Maximum age of an accepted gossip packet (5 minutes). */
#define AETHERMESH_GOSSIP_FRESHNESS_MS (5 * 60 * 1000)

/** Parsed representation of a reputation-update payload. */
typedef struct {
    char    reporter_uhid[256];
    char    target_uhid[256];
    double  score_delta;
    int64_t timestamp_ms;
    char    reason[128];
} AetherMeshReputationUpdatePayload;

/**
 * Callbacks the gossip service needs from the host application.
 * All function pointers must be non-NULL when passed to aethermesh_gossip_create.
 */
typedef struct {
    /** UHID of the local node (must outlive the gossip service). */
    const char *local_uhid;

    /**
     * Called to broadcast a serialized JSON packet to all peers.
     * Returns the number of peers the packet was delivered to.
     */
    int (*broadcast)(const char *json_packet, void *ctx);
    void *broadcast_ctx;

    /**
     * Called to sign a JSON packet string.
     * Must write the signed JSON into out_buf (max out_len bytes).
     * Returns true on success.
     */
    bool (*sign_packet)(const char *json_in, char *json_out, size_t out_len, void *ctx);
    void *sign_ctx;

    /**
     * Called to verify a JSON packet string against the sender's public key.
     * Returns true if the signature is valid.
     */
    bool (*verify_packet)(const char *json_packet,
                          const uint8_t *sender_pub_key, size_t key_len,
                          void *ctx);
    void *verify_ctx;
} AetherMeshGossipCallbacks;

/** Opaque gossip service handle. */
typedef struct AetherMeshGossipService AetherMeshGossipService;

/**
 * Create a new gossip service backed by the given reputation store.
 * callbacks must be fully populated and must outlive the returned service.
 * Returns NULL only on allocation failure.
 */
AetherMeshGossipService *aethermesh_gossip_create(
    AetherMeshNodeReputationService *reputation,
    const AetherMeshGossipCallbacks *callbacks
);

/**
 * Free all resources associated with the gossip service.
 */
void aethermesh_gossip_destroy(AetherMeshGossipService *svc);

/**
 * Build, sign, and broadcast a ReputationUpdate packet for target_uhid.
 * score_delta is clamped to [-1.0, 1.0] before transmission.
 * Returns the number of peers delivered, or -1 on error.
 */
int aethermesh_gossip_broadcast(
    AetherMeshGossipService *svc,
    const char *target_uhid,
    double score_delta,
    const char *reason
);

/**
 * Process an inbound ReputationUpdate JSON packet from a peer.
 * Verifies the signature, checks freshness and self-origin, then applies
 * the delta weighted by the reporter's local reputation score.
 * Returns true if the packet was accepted and applied.
 */
bool aethermesh_gossip_handle(
    AetherMeshGossipService *svc,
    const char *json_packet,
    const uint8_t *sender_pub_key,
    size_t key_len
);

#ifdef __cplusplus
}
#endif

#endif /* AETHERMESH_GOSSIP_H */
