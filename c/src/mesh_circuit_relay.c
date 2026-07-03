// SPDX-License-Identifier: MIT
// Circuit-relay-v2 as an auto-selected serverless-fallback transport — see
// aethernet/mesh_circuit_relay.h. Faithful port of go/circuitrelay/transport_service.go
// (TransportService) + factory.go (Create) and the C# CircuitRelayTransportService +
// MeshCircuitRelay.Create. The relay ENGINE (aethernet/relay_transport.h) stays the single
// source of truth for all relay behaviour; this file only presents it through the generic
// transport vtable (aethernet/transport.h) and bridges the engine's on-data callback to the
// transport's data-received callback. It never touches the wire format.

#define _POSIX_C_SOURCE 200809L

#include "aethernet/mesh_circuit_relay.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

// ── adapter state (the transport's opaque handle) ───────────────────────────

typedef struct {
    aethernet_relay_transport_t *engine;      // the wrapped relay engine
    bool owns_engine;                         // factory-created: destroy() frees the engine
    aethernet_relay_mesh_link_t *link;        // factory-created mesh link (owned) or NULL
    bool owns_link;                           // factory-created: destroy() frees the link

    // Transport-level data-received callback (set via set_on_data_received). Read on the engine's
    // delivery thread (a detached hop thread), so it is guarded like the Go adapter's RWMutex.
    pthread_mutex_t cb_mu;
    aethernet_transport_on_data_received on_data;
    void *on_data_ud;

    // Live EWMA metrics for the predictive selector / manager ranking.
    aethernet_transport_metrics_t metrics;
} relay_adapter_t;

// The vtable name pointer identifies a circuit-relay transport. Every relay transport's vtable
// carries THIS exact pointer as its name, so is_relay_transport() is a cheap pointer compare that
// also survives even if a caller copied the string elsewhere.
static const char *const kRelayTransportName = AETHERNET_CIRCUIT_RELAY_TRANSPORT_NAME;

// ── engine -> transport data bridge ─────────────────────────────────────────

// Trampoline registered on the engine. Runs on the engine's delivery thread when tunnelled DATA is
// delivered to this node as the final destination; forwards to the transport-level callback under
// the lock, then records one successful sample for the selector.
static void relay_engine_on_data(const char *sender, const uint8_t *data,
                                 uint32_t len, void *user_data) {
    relay_adapter_t *a = (relay_adapter_t *)user_data;
    if (!a) return;

    pthread_mutex_lock(&a->cb_mu);
    aethernet_transport_on_data_received cb = a->on_data;
    void *ud = a->on_data_ud;
    pthread_mutex_unlock(&a->cb_mu);

    if (cb) cb(sender, data, (size_t)len, ud);

    // Received delivery is a live signal too; keep the EWMA warm (RTT unknown on receive → small
    // constant, same spirit as the in-process transport recording 1 ms).
    aethernet_transport_metrics_record_sample(&a->metrics, 1, true, (uint64_t)len);
}

// ── vtable methods ──────────────────────────────────────────────────────────

// send: establish a bridge if needed, then tunnel DATA (all inside the engine). Returns the
// engine's verdict; a false lets a manager fall through to a cheaper... (there is none cheaper than
// this fallback, so a false there means overall failure). Records one metrics sample either way.
static bool relay_send(void *handle, const char *peer_uhid,
                       const uint8_t *data, size_t data_len) {
    relay_adapter_t *a = (relay_adapter_t *)handle;
    if (!a || !a->engine || !peer_uhid || !data) return false;

    bool ok = aethernet_relay_transport_send(a->engine, peer_uhid, data, (uint32_t)data_len);
    aethernet_transport_metrics_record_sample(&a->metrics, ok ? 1 : 0, ok,
                                              ok ? (uint64_t)data_len : 0);
    return ok;
}

// is_connected: true once a relay bridge to the peer has been established.
static bool relay_is_connected(void *handle, const char *peer_uhid) {
    relay_adapter_t *a = (relay_adapter_t *)handle;
    if (!a || !a->engine || !peer_uhid) return false;
    return aethernet_relay_transport_is_connected(a->engine, peer_uhid);
}

// set_on_data_received: store the transport-level callback the engine trampoline forwards to.
static void relay_set_on_data_received(void *handle,
                                       aethernet_transport_on_data_received callback,
                                       void *user_data) {
    relay_adapter_t *a = (relay_adapter_t *)handle;
    if (!a) return;
    pthread_mutex_lock(&a->cb_mu);
    a->on_data = callback;
    a->on_data_ud = user_data;
    pthread_mutex_unlock(&a->cb_mu);
}

static aethernet_transport_metrics_t *relay_get_metrics(void *handle) {
    relay_adapter_t *a = (relay_adapter_t *)handle;
    if (!a) return NULL;
    return &a->metrics;
}

// destroy: tear down the adapter (and, when factory-created, the engine + mesh link it owns).
static void relay_destroy(void *handle) {
    relay_adapter_t *a = (relay_adapter_t *)handle;
    if (!a) return;

    // Free the engine BEFORE the mesh link: the engine's link vtable ctx is the mesh link, and a
    // destroyed engine wakes/serves nothing further, so the link is safe to free afterwards. This
    // is the same order the mesh test tears down (transports first, then links).
    if (a->owns_engine && a->engine) {
        aethernet_relay_transport_destroy(a->engine);
        a->engine = NULL;
    }
    if (a->owns_link && a->link) {
        aethernet_relay_mesh_link_destroy(a->link);
        a->link = NULL;
    }
    pthread_mutex_destroy(&a->cb_mu);
    free(a);
}

// Build the transport wrapper (vtable + handle) around a ready adapter. On failure frees `a` (its
// mutex must already be initialised) and returns NULL.
static aethernet_transport_t *build_transport(relay_adapter_t *a) {
    aethernet_transport_t *transport =
        (aethernet_transport_t *)malloc(sizeof(aethernet_transport_t));
    if (!transport) { pthread_mutex_destroy(&a->cb_mu); free(a); return NULL; }

    aethernet_transport_vtable_t *vtable =
        (aethernet_transport_vtable_t *)malloc(sizeof(aethernet_transport_vtable_t));
    if (!vtable) { free(transport); pthread_mutex_destroy(&a->cb_mu); free(a); return NULL; }

    // Zero all fields first so any future vtable additions get a sensible default.
    memset(vtable, 0, sizeof(*vtable));
    vtable->name = kRelayTransportName;                 // "Circuit Relay (v2)"
    vtable->send = relay_send;
    vtable->is_connected = relay_is_connected;
    vtable->set_on_data_received = relay_set_on_data_received;
    vtable->destroy = relay_destroy;
    vtable->get_metrics = relay_get_metrics;
    vtable->max_bandwidth_bps = AETHERNET_CIRCUIT_RELAY_MAX_BANDWIDTH_BPS; // 5 Mbit/s relayed path
    vtable->power_cost_relative = AETHERNET_CIRCUIT_RELAY_POWER_COST;      // 90 — last-resort fallback
    vtable->max_range_meters = 0;                        // internet-scope, not range-bound

    transport->vtable = vtable;
    transport->handle = a;

    // Take over the engine's on-data callback to surface tunnelled DATA through the transport.
    aethernet_relay_transport_set_on_data(a->engine, relay_engine_on_data, a);
    return transport;
}

// Allocate + init a bare adapter around `engine`. Returns NULL on allocation/mutex failure.
static relay_adapter_t *new_adapter(aethernet_relay_transport_t *engine) {
    relay_adapter_t *a = (relay_adapter_t *)calloc(1, sizeof(relay_adapter_t));
    if (!a) return NULL;
    a->engine = engine;
    if (pthread_mutex_init(&a->cb_mu, NULL) != 0) { free(a); return NULL; }
    aethernet_transport_metrics_init(&a->metrics);
    return a;
}

// ── public: wrap an existing engine ─────────────────────────────────────────

aethernet_transport_t *aethernet_mesh_circuit_relay_wrap(aethernet_relay_transport_t *engine) {
    if (!engine) return NULL;
    relay_adapter_t *a = new_adapter(engine);
    if (!a) return NULL;
    a->owns_engine = false;   // caller keeps ownership of the engine
    a->owns_link = false;
    a->link = NULL;
    return build_transport(a);
}

// ── public: factory (mirrors C# MeshCircuitRelay.Create) ────────────────────

aethernet_transport_t *aethernet_mesh_circuit_relay_create(
    const char *local_uhid,
    aethernet_relay_mesh_send_one_hop_fn send_one_hop, void *send_ctx,
    aethernet_relay_mesh_can_reach_fn can_reach, void *reach_ctx,
    aethernet_relay_options_t options,
    aethernet_relay_mesh_link_t **out_link) {
    if (out_link) *out_link = NULL;
    if (!local_uhid || !send_one_hop || !can_reach) return NULL;

    // 1. Production mesh RelayLink over the host's two one-hop callbacks.
    aethernet_relay_mesh_link_t *link =
        aethernet_relay_mesh_link_new(local_uhid, send_one_hop, send_ctx, can_reach, reach_ctx);
    if (!link) return NULL;

    // 2. Relay engine bound to that link's engine-facing vtable. The vtable is by-value; the engine
    //    copies what it needs but the link itself must outlive the engine — it does (we own both).
    aethernet_relay_link_t link_vt = aethernet_relay_mesh_link_as_link(link);
    aethernet_relay_transport_t *engine =
        aethernet_relay_transport_new(local_uhid, &link_vt, options, NULL, NULL);
    if (!engine) { aethernet_relay_mesh_link_destroy(link); return NULL; }

    // 3. Bind the engine so inbound CircuitRelayControl packets fed to the link reach it.
    aethernet_relay_mesh_link_bind_transport(link, engine);

    // 4. Wrap the engine as a transport; this adapter owns BOTH the engine and the link.
    relay_adapter_t *a = new_adapter(engine);
    if (!a) {
        aethernet_relay_transport_destroy(engine);
        aethernet_relay_mesh_link_destroy(link);
        return NULL;
    }
    a->owns_engine = true;
    a->owns_link = true;
    a->link = link;

    aethernet_transport_t *transport = build_transport(a);
    if (!transport) {
        // build_transport already freed the adapter + its mutex on failure, but NOT the engine/link
        // it was to own — free them here to avoid a leak.
        aethernet_relay_transport_destroy(engine);
        aethernet_relay_mesh_link_destroy(link);
        return NULL;
    }

    if (out_link) *out_link = link;
    return transport;
}

// ── relay/target-role passthroughs ──────────────────────────────────────────

bool aethernet_mesh_circuit_relay_is_relay_transport(const aethernet_transport_t *transport) {
    return transport && transport->vtable && transport->vtable->name == kRelayTransportName;
}

// Fetch the adapter handle iff `transport` is a circuit-relay transport, else NULL.
static relay_adapter_t *as_adapter(aethernet_transport_t *transport) {
    if (!aethernet_mesh_circuit_relay_is_relay_transport(transport)) return NULL;
    return (relay_adapter_t *)transport->handle;
}

aethernet_relay_transport_t *aethernet_mesh_circuit_relay_engine(aethernet_transport_t *transport) {
    relay_adapter_t *a = as_adapter(transport);
    return a ? a->engine : NULL;
}

bool aethernet_mesh_circuit_relay_reserve(aethernet_transport_t *transport, const char *relay) {
    relay_adapter_t *a = as_adapter(transport);
    if (!a || !a->engine || !relay) return false;
    return aethernet_relay_transport_reserve(a->engine, relay);
}

bool aethernet_mesh_circuit_relay_set_route(aethernet_transport_t *transport,
                                            const char *dest, const char *relay) {
    relay_adapter_t *a = as_adapter(transport);
    if (!a || !a->engine || !dest || !relay) return false;
    return aethernet_relay_transport_set_route(a->engine, dest, relay);
}

int aethernet_mesh_circuit_relay_active_bridge_count(aethernet_transport_t *transport) {
    relay_adapter_t *a = as_adapter(transport);
    if (!a || !a->engine) return 0;
    return aethernet_relay_transport_active_bridge_count(a->engine);
}

int aethernet_mesh_circuit_relay_active_reservation_count(aethernet_transport_t *transport) {
    relay_adapter_t *a = as_adapter(transport);
    if (!a || !a->engine) return 0;
    return aethernet_relay_transport_active_reservation_count(a->engine);
}
