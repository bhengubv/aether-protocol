// SPDX-License-Identifier: MIT
// Production mesh RelayLink — see aethernet/relay_mesh_link.h. Faithful port of
// go/circuitrelay/meshlink.go (MeshRelayLink) and the C# / Swift MeshRelayLink.

#define _POSIX_C_SOURCE 200809L

#include "aethernet/relay_mesh_link.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

struct aethernet_relay_mesh_link {
    char *local_uhid;                                 // owned copy; stamped as packet source
    aethernet_relay_mesh_send_one_hop_fn send_one_hop;
    void *send_ctx;
    aethernet_relay_mesh_can_reach_fn can_reach;
    void *reach_ctx;

    pthread_mutex_t mu;                               // guards `transport`
    aethernet_relay_transport_t *transport;           // bound engine (borrowed)
};

// ── lifecycle ───────────────────────────────────────────────────────────────

aethernet_relay_mesh_link_t *aethernet_relay_mesh_link_new(
    const char *local_uhid,
    aethernet_relay_mesh_send_one_hop_fn send_one_hop, void *send_ctx,
    aethernet_relay_mesh_can_reach_fn can_reach, void *reach_ctx) {
    if (!local_uhid || !send_one_hop || !can_reach) return NULL;

    aethernet_relay_mesh_link_t *link =
        (aethernet_relay_mesh_link_t *)calloc(1, sizeof(*link));
    if (!link) return NULL;

    size_t n = strlen(local_uhid);
    link->local_uhid = (char *)malloc(n + 1);
    if (!link->local_uhid) { free(link); return NULL; }
    memcpy(link->local_uhid, local_uhid, n + 1);

    link->send_one_hop = send_one_hop;
    link->send_ctx = send_ctx;
    link->can_reach = can_reach;
    link->reach_ctx = reach_ctx;
    link->transport = NULL;

    if (pthread_mutex_init(&link->mu, NULL) != 0) {
        free(link->local_uhid);
        free(link);
        return NULL;
    }
    return link;
}

void aethernet_relay_mesh_link_destroy(aethernet_relay_mesh_link_t *link) {
    if (!link) return;
    pthread_mutex_destroy(&link->mu);
    free(link->local_uhid);
    free(link);
}

void aethernet_relay_mesh_link_bind_transport(aethernet_relay_mesh_link_t *link,
                                              aethernet_relay_transport_t *transport) {
    if (!link) return;
    pthread_mutex_lock(&link->mu);
    link->transport = transport;
    pthread_mutex_unlock(&link->mu);
}

// ── engine-facing vtable (aethernet_relay_link_t) ───────────────────────────

// send_frame: wrap the raw relay frame in a CircuitRelayControl MeshPacket and hand it
// one hop to `node` via the host callback. The packet is freed before returning, so an
// async host must clone it. Returns false on any allocation/setter failure.
static bool mesh_link_send_frame(void *ctx, const char *node,
                                 const uint8_t *frame, uint32_t len) {
    aethernet_relay_mesh_link_t *link = (aethernet_relay_mesh_link_t *)ctx;
    if (!link || !node) return false;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;

    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL;
    pkt->ttl = 1; // relay frames travel exactly one hop; end-to-end routing is the engine's job

    bool ok = aethernet_packet_set_source_uhid(pkt, link->local_uhid)
           && aethernet_packet_set_destination_uhid(pkt, node)
           && aethernet_packet_set_payload(pkt, frame, (size_t)len);
    if (!ok) { aethernet_packet_free(pkt); return false; }

    bool sent = link->send_one_hop(link->send_ctx, pkt);
    aethernet_packet_free(pkt);
    return sent;
}

static bool mesh_link_can_reach(void *ctx, const char *node) {
    aethernet_relay_mesh_link_t *link = (aethernet_relay_mesh_link_t *)ctx;
    if (!link) return false;
    return link->can_reach(link->reach_ctx, node);
}

aethernet_relay_link_t aethernet_relay_mesh_link_as_link(aethernet_relay_mesh_link_t *link) {
    aethernet_relay_link_t vt;
    vt.ctx = link;
    vt.send_frame = mesh_link_send_frame;
    vt.can_reach = mesh_link_can_reach;
    return vt;
}

// ── host receive path ───────────────────────────────────────────────────────

void aethernet_relay_mesh_link_handle_incoming_packet(aethernet_relay_mesh_link_t *link,
                                                      const aethernet_mesh_packet_t *packet) {
    if (!link || !packet) return;
    if (packet->type != (uint8_t)AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL) return;

    pthread_mutex_lock(&link->mu);
    aethernet_relay_transport_t *t = link->transport;
    pthread_mutex_unlock(&link->mu);
    if (!t) return;

    // Feed the wrapped relay frame back into the engine, attributed to the packet's
    // source (the one-hop neighbour that sent it). payload may be NULL/0 for a frame
    // carrying no body; the engine's decode handles that.
    aethernet_relay_transport_on_frame(t, packet->source_uhid,
                                       packet->payload, packet->payload_len);
}
