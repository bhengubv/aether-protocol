// SPDX-License-Identifier: MIT
//
// Production RelayLink that carries native circuit-relay-v2 frames one hop over the
// real mesh — the C equivalent of the C# / Go / Python / TS / Rust / Kotlin / Swift
// MeshRelayLink. Each raw relay frame produced by the relay ENGINE
// (aethernet/relay_transport.h) is wrapped in an aethernet_mesh_packet_t of type
// AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL (source = this node, dest = the one-hop
// neighbour, ttl = 1, payload = the frame) and handed to the host's "send one hop"
// callback. Inbound CircuitRelayControl packets from the host's receive path are fed
// back into the engine via aethernet_relay_mesh_link_handle_incoming_packet.
//
// The seam to the real transport is the two host callbacks (BLE / Wi-Fi Direct / WebRTC
// / the HTTP relay). Like the reference ports, it never calls a radio directly and never
// recurses through itself — the host's one-hop send must exclude the circuit-relay
// transport.
//
// Unlike the Go / C# RelayLink interface (which exposes an OnFrame handler on the link),
// the C relay ENGINE has NO onFrame on aethernet_relay_link_t: inbound frames are fed
// straight into the transport with aethernet_relay_transport_on_frame(). This mesh link
// therefore holds a bound transport (aethernet_relay_mesh_link_bind_transport) and
// forwards decoded CircuitRelayControl payloads to it on receive.

#ifndef AETHERNET_RELAY_MESH_LINK_H
#define AETHERNET_RELAY_MESH_LINK_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/relay_transport.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct aethernet_relay_mesh_link aethernet_relay_mesh_link_t;

// Host "send one hop" callback: hand a CircuitRelayControl MeshPacket to a directly
// connected neighbour. `packet` is borrowed for the duration of the call only — the mesh
// link frees it right after this returns, so an implementation that delivers
// asynchronously MUST clone it (aethernet_packet_clone). Returns true if handed off.
typedef bool (*aethernet_relay_mesh_send_one_hop_fn)(void *ctx,
                                                     const aethernet_mesh_packet_t *packet);

// Host reachability callback: does this node currently have a direct one-hop link to
// `node`? Mirrors aethernet_relay_link_t.can_reach.
typedef bool (*aethernet_relay_mesh_can_reach_fn)(void *ctx, const char *node);

// ── lifecycle ───────────────────────────────────────────────────────────────

// Create a mesh link.
//   local_uhid    — this node's UHID (stamped as the packet source; copied).
//   send_one_hop  — sends a CircuitRelayControl MeshPacket to a connected peer.
//   send_ctx      — user data passed to send_one_hop.
//   can_reach     — reports a direct one-hop link to a peer.
//   reach_ctx     — user data passed to can_reach.
// Returns NULL on allocation failure or if any required argument is NULL.
aethernet_relay_mesh_link_t *aethernet_relay_mesh_link_new(
    const char *local_uhid,
    aethernet_relay_mesh_send_one_hop_fn send_one_hop, void *send_ctx,
    aethernet_relay_mesh_can_reach_fn can_reach, void *reach_ctx);

// Destroy a mesh link (frees its state; does not touch the bound transport).
void aethernet_relay_mesh_link_destroy(aethernet_relay_mesh_link_t *link);

// Bind the relay ENGINE this link feeds on receive. Must be called (typically right
// after aethernet_relay_transport_new) before any inbound packet can be delivered.
// `transport` is borrowed; it must outlive the link.
void aethernet_relay_mesh_link_bind_transport(aethernet_relay_mesh_link_t *link,
                                              aethernet_relay_transport_t *transport);

// ── engine seam ─────────────────────────────────────────────────────────────

// Return a by-value aethernet_relay_link_t vtable whose ctx is this mesh link. Pass the
// result to aethernet_relay_transport_new. Its send_frame wraps the raw frame in a
// CircuitRelayControl MeshPacket and calls send_one_hop; its can_reach forwards to the
// host callback. The mesh link must outlive any transport created with this vtable.
aethernet_relay_link_t aethernet_relay_mesh_link_as_link(aethernet_relay_mesh_link_t *link);

// Feed an inbound MeshPacket from the host's receive path. If its type is
// AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL, its payload is delivered to the bound
// transport as a raw relay frame via aethernet_relay_transport_on_frame(t,
// packet->source_uhid, packet->payload, packet->payload_len). Any other type is ignored.
// `packet` is borrowed for the call only.
void aethernet_relay_mesh_link_handle_incoming_packet(aethernet_relay_mesh_link_t *link,
                                                      const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_RELAY_MESH_LINK_H
