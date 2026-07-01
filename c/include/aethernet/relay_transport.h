// SPDX-License-Identifier: MIT
//
// Native circuit-relay-v2 ENGINE (C11 + pthreads) — the decentralised, no-libp2p
// equivalent of libp2p's circuit-relay-v2. Any AetherNet node can act as a relay:
// a node that cannot reach a peer directly routes through a third node reachable
// to both. Built on the wire codec in aethernet/circuit_relay.h. Faithful port of
// go/circuitrelay/transport.go and src/AetherNet.Transport/CircuitRelay/
// CircuitRelayTransportService.cs.
//
// Three roles, all in this one transport (a node can be any/all at once):
//   * Target — reserves capacity on a relay (aethernet_relay_transport_reserve) so
//     peers can reach this node through it.
//   * Client — aethernet_relay_transport_send to a peer for which a relay route is
//     known (aethernet_relay_transport_set_route) performs the CONNECT handshake
//     then tunnels DATA.
//   * Relay — grants reservations, bridges CONNECT->STOP, and forwards DATA between
//     the two legs under a data/duration budget.
//
// One hop of a frame is carried by an injected RelayLink vtable (BLE, Wi-Fi Direct,
// WebRTC, the HTTP relay, or an in-process link in tests). The delivery side calls
// aethernet_relay_transport_on_frame(t, from, frame, len) when a raw frame arrives.

#ifndef AETHERNET_RELAY_TRANSPORT_H
#define AETHERNET_RELAY_TRANSPORT_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// ── RelayLink: the one-hop seam ─────────────────────────────────────────────
//
// The transport-agnostic link a Transport uses to exchange raw relay frames with
// directly-reachable nodes. Mirrors the Go RelayLink / C# IRelayLink interface.
typedef struct aethernet_relay_link {
    void *ctx;
    // Send a raw relay frame to a node reachable in one hop. Returns true if the
    // frame was handed to that node's link. `frame` is borrowed for the call only.
    bool (*send_frame)(void *ctx, const char *node, const uint8_t *frame, uint32_t len);
    // Whether this node currently has a direct one-hop link to `node`.
    bool (*can_reach)(void *ctx, const char *node);
} aethernet_relay_link_t;

// ── Options: policy/tuning (mirrors Go Options / C# CircuitRelayOptions) ─────
typedef struct {
    int64_t reservation_ttl_ms;        // how long a granted reservation stays valid
    int max_reservations;              // max concurrent reservations held as relay
    int max_bridges;                   // max concurrent bridges serviced as relay
    int64_t bridge_data_limit_bytes;   // per-bridge data budget (0 = unlimited)
    int32_t bridge_duration_limit_seconds; // per-bridge duration budget (0 = unlimited)
    int64_t connect_timeout_ms;        // client wait for a CONNECT confirmation
    int64_t reserve_timeout_ms;        // client wait for a RESERVE confirmation
    bool act_as_relay;                 // grant reservations + bridge others' traffic
} aethernet_relay_options_t;

// Same defaults as the Go/C# reference (30-min TTL, 128 caps, 10s waits, relay on).
aethernet_relay_options_t aethernet_relay_options_default(void);

// ── on-data callback: endpoint delivery ─────────────────────────────────────
// Invoked when tunnelled data is delivered to this node as an endpoint
// (sender UHID, payload bytes/len, user data). `data` is borrowed for the call.
typedef void (*aethernet_relay_on_data_fn)(const char *sender, const uint8_t *data,
                                           uint32_t len, void *user_data);

// Injectable monotonic-ish clock (unix milliseconds). Used for reservation expiry
// and bridge duration deadlines; injectable so expiry is deterministic in tests.
typedef int64_t (*aethernet_relay_now_fn)(void *user_data);

typedef struct aethernet_relay_transport aethernet_relay_transport_t;

// ── lifecycle ───────────────────────────────────────────────────────────────

// Create a Transport bound to `link` (borrowed; must outlive the transport).
// `now` may be NULL (defaults to wall-clock CLOCK_REALTIME milliseconds);
// `now_ud` is passed to it. Returns NULL on allocation failure. Free with destroy.
aethernet_relay_transport_t *aethernet_relay_transport_new(
    const char *local_uhid,
    const aethernet_relay_link_t *link,
    aethernet_relay_options_t options,
    aethernet_relay_now_fn now,
    void *now_ud);

// Destroy a Transport: wakes any pending waiters (as failed), frees all state.
void aethernet_relay_transport_destroy(aethernet_relay_transport_t *t);

// Register the endpoint-delivery callback (sender UHID, payload).
void aethernet_relay_transport_set_on_data(aethernet_relay_transport_t *t,
                                           aethernet_relay_on_data_fn cb, void *user_data);

// Record that `dest` is reachable via `relay` (in production from the directory /
// reservation gossip; tests set it directly). Returns false if the route table is full.
bool aethernet_relay_transport_set_route(aethernet_relay_transport_t *t,
                                         const char *dest, const char *relay);

// ── target / client API ─────────────────────────────────────────────────────

// Reserve capacity on `relay` so peers can reach this node through it. Blocks up to
// reserve_timeout_ms for the relay's confirmation. Returns true once confirmed.
bool aethernet_relay_transport_reserve(aethernet_relay_transport_t *t, const char *relay);

// Deliver `data` to `peer`, establishing a relay bridge first if needed (CONNECT
// handshake, then a tunnelled DATA frame). Returns true if the frame was sent.
bool aethernet_relay_transport_send(aethernet_relay_transport_t *t,
                                    const char *peer, const uint8_t *data, uint32_t len);

// ── inbound entry point ─────────────────────────────────────────────────────

// Feed a raw relay frame that arrived from directly-reachable node `from`. This is
// what the delivery side of a RelayLink calls. `frame` is borrowed for the call.
void aethernet_relay_transport_on_frame(aethernet_relay_transport_t *t,
                                        const char *from, const uint8_t *frame, uint32_t len);

// ── diagnostics (mirror the Go test helpers) ────────────────────────────────

int aethernet_relay_transport_active_bridge_count(aethernet_relay_transport_t *t);
int aethernet_relay_transport_active_reservation_count(aethernet_relay_transport_t *t);
bool aethernet_relay_transport_is_connected(aethernet_relay_transport_t *t, const char *peer);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_RELAY_TRANSPORT_H
