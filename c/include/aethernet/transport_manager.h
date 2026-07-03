// SPDX-License-Identifier: MIT
//
// Minimal multi-transport manager (gap 2). The C counterpart of go/transport/manager.go
// (transport.Manager) and the real selection path of the C# TransportManager
// (src/AetherNet.Transport/Services/TransportManager.cs).
//
// It does two things and nothing more:
//   * holds its transports sorted ASCENDING by vtable->power_cost_relative, so the cheapest is
//     tried first and an expensive last-resort transport (the circuit relay, cost 90) is only
//     reached after every cheaper one has declined; and
//   * aethernet_transport_manager_send() falls through the ordered transports until one returns
//     true or all decline.
//
// This is exactly the C# manager's "step 6: additional transports, sorted by PowerCostRelative
// (ascending), fall through until one succeeds". Typed BLE / Wi-Fi Direct / NearLink slots are not
// modelled — on this C SDK every transport is registered as an additional transport and ordered
// purely by power cost, which is what makes the relay a genuine auto-selected fallback rather than a
// hand-wired special case.
//
// Inbound: the manager subscribes to each transport's data-received callback and re-raises every
// delivery through a SINGLE callback tagged with the name of the transport that carried it (the
// "via" tag) — mirroring the C# TransportManager.DataReceived (sender, data, transportName) event
// and the Go Manager's OnDataReceived. Tagging with the selected transport's name is what makes the
// auto-selection observable in a test.
//
// The manager BORROWS its transports: it never destroys them (they may outlive it or be shared).
// It takes over each transport's data-received callback while alive.

#ifndef AETHERNET_TRANSPORT_MANAGER_H
#define AETHERNET_TRANSPORT_MANAGER_H

#include <stddef.h>
#include <stdbool.h>
#include <stdint.h>

#include "aethernet/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct aethernet_transport_manager aethernet_transport_manager_t;

// Manager data-received callback: invoked when ANY managed transport delivers data to this node.
// Arguments are (sender UHID, payload bytes/len, name of the transport that carried it, user data).
// The `via` tag proves which transport the manager selected on the receive side. `sender`/`via` are
// borrowed for the call only; copy them if retained. `data` is borrowed for the call only.
typedef void (*aethernet_transport_manager_on_data_fn)(const char *sender,
                                                       const uint8_t *data,
                                                       size_t data_len,
                                                       const char *via,
                                                       void *user_data);

// ── lifecycle ───────────────────────────────────────────────────────────────

// Build a manager over `count` transports (borrowed; each must outlive the manager). They are
// copied into an internal array ordered ascending by vtable->power_cost_relative (a STABLE sort:
// equal costs keep registration order, matching the C# OrderBy and Go SliceStable). The manager
// installs its own trampoline as each transport's data-received callback, so do not register a
// competing callback on a managed transport while the manager is alive.
//
// `transports` may be NULL only when `count` is 0. A NULL element is skipped. Returns NULL on
// allocation failure.
aethernet_transport_manager_t *aethernet_transport_manager_new(
    aethernet_transport_t *const *transports, size_t count);

// Destroy the manager: detaches its trampoline from each managed transport (restores a NULL
// callback) and frees its own state. Does NOT destroy the transports themselves.
void aethernet_transport_manager_destroy(aethernet_transport_manager_t *mgr);

// ── receive ─────────────────────────────────────────────────────────────────

// Register the callback invoked when any managed transport delivers data to this node. Passing NULL
// clears it. Replaces any previously registered callback.
void aethernet_transport_manager_set_on_data(aethernet_transport_manager_t *mgr,
                                             aethernet_transport_manager_on_data_fn cb,
                                             void *user_data);

// ── send ────────────────────────────────────────────────────────────────────

// Send `data` to `peer_uhid`, trying each managed transport in ascending power-cost order until one
// reports delivery. Returns true on the first transport whose send() returns true; false if every
// transport declines (a transport that can't reach the peer right now simply returns false and the
// manager moves to the next candidate — identical to the C#/Go fall-through). Returns false if
// `mgr`, `peer_uhid`, or `data` is NULL.
bool aethernet_transport_manager_send(aethernet_transport_manager_t *mgr,
                                     const char *peer_uhid,
                                     const uint8_t *data, size_t data_len);

// ── introspection ───────────────────────────────────────────────────────────

// Number of transports the manager holds (post-sort, NULL elements excluded).
size_t aethernet_transport_manager_count(const aethernet_transport_manager_t *mgr);

// The transport at ordered index `i` (0 == lowest power cost), or NULL if out of range. The
// returned pointer is borrowed — the manager still owns nothing and frees nothing.
aethernet_transport_t *aethernet_transport_manager_at(const aethernet_transport_manager_t *mgr,
                                                     size_t i);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_TRANSPORT_MANAGER_H
