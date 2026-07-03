// SPDX-License-Identifier: MIT
//
// Circuit-relay-v2 as an auto-selected serverless-fallback TRANSPORT (gap 2).
//
// This is the C counterpart of the C# MeshCircuitRelay factory + CircuitRelayTransportService
// (src/AetherNet.Transport/CircuitRelay/*.cs), the Go circuitrelay.Create + TransportService
// (go/circuitrelay/factory.go + transport_service.go), and the Python / TypeScript ports. It
// presents the transport-agnostic circuit-relay ENGINE (aethernet/relay_transport.h) through the
// generic transport vtable (aethernet/transport.h) so the minimal transport manager
// (aethernet/transport_manager.h) can auto-select it EXACTLY like BLE / Wi-Fi Direct / WebRTC —
// and, because its power cost is 90 (just below the HTTP relay's last-resort 100), only AFTER
// every cheaper direct transport has declined. That is what makes the relay a genuine
// serverless fallback rather than a hand-wired special case.
//
// NOTHING here touches the wire format: the engine remains the single source of truth for all
// relay behaviour (reservations, bridging, budgets), and the frame codec (aethernet/circuit_relay.h)
// and the CircuitRelayControl MeshPacket wrapping (aethernet/relay_mesh_link.h) are used verbatim.
//
// Roles are unchanged from the engine (a node can be any/all at once):
//   * Target — reserves capacity on a relay (aethernet_mesh_circuit_relay_reserve) so peers can
//     reach this node through it.
//   * Client — the transport's send() to a peer with a known relay route
//     (aethernet_mesh_circuit_relay_set_route) establishes a bridge then tunnels DATA.
//   * Relay — grants reservations, bridges CONNECT->STOP, forwards DATA under a budget.
//
// The transport reports name "Circuit Relay (v2)" and power_cost_relative 90, byte-for-byte the
// same strings/constants as every other language port.

#ifndef AETHERNET_MESH_CIRCUIT_RELAY_H
#define AETHERNET_MESH_CIRCUIT_RELAY_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/relay_mesh_link.h"
#include "aethernet/relay_transport.h"
#include "aethernet/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// Human-readable transport name. Byte-for-byte identical to the C# / Go / Python / TS
// CircuitRelayTransportService.Name, so a manager that tags received data with the carrying
// transport's name makes the auto-selection observable.
#define AETHERNET_CIRCUIT_RELAY_TRANSPORT_NAME "Circuit Relay (v2)"

// Relative power cost of the circuit-relay transport. Relayed traffic is costly (an extra hop
// through a third node), so it sits just below the HTTP relay's last-resort cost of 100 —
// high enough that a manager only falls through to it once every cheaper direct transport is
// exhausted. Mirrors C# CircuitRelayTransportService.PowerCostRelative == 90.
#define AETHERNET_CIRCUIT_RELAY_POWER_COST 90

// Conservative relayed-path bandwidth in bits/sec (below a direct link, since every byte crosses
// an extra hop). Matches the C# / Go reference (5 Mbit/s).
#define AETHERNET_CIRCUIT_RELAY_MAX_BANDWIDTH_BPS 5000000

// ── adapter over an existing engine ─────────────────────────────────────────

// Wrap an existing relay ENGINE as a generic transport. The returned aethernet_transport_t has
// vtable->name == "Circuit Relay (v2)", power_cost_relative == 90, and its send() calls
// aethernet_relay_transport_send() (establish-bridge-then-tunnel-DATA); inbound tunnelled DATA
// delivered to this node surfaces through the transport's data-received callback
// (aethernet_transport_set_on_data_received). The adapter takes over the engine's on-data
// callback — do NOT also call aethernet_relay_transport_set_on_data on `engine` afterwards.
//
// Ownership: the returned transport does NOT own `engine` (the caller keeps and frees it). Use
// aethernet_transport_destroy() to free just the adapter; the engine is left intact (it may still
// be servicing bridges for other roles). For the all-in-one lifecycle prefer the factory below.
//
// Returns NULL on allocation failure or if `engine` is NULL.
aethernet_transport_t *aethernet_mesh_circuit_relay_wrap(aethernet_relay_transport_t *engine);

// ── factory (mirrors C# MeshCircuitRelay.Create) ────────────────────────────

// Everything a host needs to run the relay as a transport, wired in one call. Builds a production
// mesh RelayLink (aethernet/relay_mesh_link.h) over the two host callbacks, stands up the relay
// engine on it, binds them, and wraps the engine as a transport. The host then:
//   1. registers the returned transport with its transport manager
//      (aethernet_transport_manager_new) — it is auto-selected as the last-resort fallback at
//      power cost 90; and
//   2. routes every received CircuitRelayControl MeshPacket to the returned mesh link via
//      aethernet_relay_mesh_link_handle_incoming_packet.
//
// Arguments mirror MeshCircuitRelay.Create:
//   local_uhid   — this node's UHID (stamped as the relay-packet source; copied by the link).
//   send_one_hop — sends a CircuitRelayControl MeshPacket to a directly-connected peer; true if
//                  handed off. MUST exclude the circuit-relay transport itself so a frame never
//                  recurses back through this transport.
//   send_ctx     — user data passed to send_one_hop.
//   can_reach    — reports whether this node has a direct one-hop link to a peer.
//   reach_ctx    — user data passed to can_reach.
//   options      — engine policy/tuning; pass aethernet_relay_options_default() for the
//                  C#-equivalent defaults.
//   out_link     — receives the mesh link the host must feed inbound CircuitRelayControl packets
//                  into (see step 2). Borrowed reference — owned by the returned transport; do
//                  NOT destroy it separately. May be NULL if the caller does not need it.
//
// Ownership: UNLIKE aethernet_mesh_circuit_relay_wrap, the factory-created transport OWNS the
// engine AND the mesh link it created; aethernet_transport_destroy() tears down all three in the
// correct order. Returns NULL (and leaves *out_link unset) on any allocation failure.
aethernet_transport_t *aethernet_mesh_circuit_relay_create(
    const char *local_uhid,
    aethernet_relay_mesh_send_one_hop_fn send_one_hop, void *send_ctx,
    aethernet_relay_mesh_can_reach_fn can_reach, void *reach_ctx,
    aethernet_relay_options_t options,
    aethernet_relay_mesh_link_t **out_link);

// ── relay/target-role passthroughs ──────────────────────────────────────────
//
// The generic transport contract only covers send/is_connected/on-data. Reserve (advertise
// reachability via a relay), set_route (learn a peer is reachable via a relay), and the bridge /
// reservation diagnostics are relay-specific, so they are exposed here as thin passthroughs to the
// wrapped engine — the C equivalent of Go's TransportService.Engine() accessor. Each returns a
// harmless default (false / 0) if `transport` is not a circuit-relay transport.

// Reserve capacity on `relay` so peers can reach this node through it. Blocks up to the engine's
// reserve timeout for confirmation. Returns true once confirmed.
bool aethernet_mesh_circuit_relay_reserve(aethernet_transport_t *transport, const char *relay);

// Record that `dest` is reachable via `relay`. In production this comes from the directory /
// reservation gossip; tests set it directly. Returns false if the route table is full.
bool aethernet_mesh_circuit_relay_set_route(aethernet_transport_t *transport,
                                            const char *dest, const char *relay);

// Number of bridges this node is currently servicing as a relay (diagnostics/tests).
int aethernet_mesh_circuit_relay_active_bridge_count(aethernet_transport_t *transport);

// Number of reservations this node is currently holding as a relay (diagnostics/tests).
int aethernet_mesh_circuit_relay_active_reservation_count(aethernet_transport_t *transport);

// The underlying engine, or NULL if `transport` is not a circuit-relay transport. Lets callers
// drive any engine API not surfaced above without breaking the ownership model.
aethernet_relay_transport_t *aethernet_mesh_circuit_relay_engine(aethernet_transport_t *transport);

// True if `transport` was produced by wrap()/create() above (its vtable name matches). Used by the
// passthroughs to guard the handle cast; exposed for callers that hold a heterogeneous transport.
bool aethernet_mesh_circuit_relay_is_relay_transport(const aethernet_transport_t *transport);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_MESH_CIRCUIT_RELAY_H
