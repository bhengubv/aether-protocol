// SPDX-License-Identifier: MIT
// Minimal multi-transport manager — see aethernet/transport_manager.h. The C counterpart of
// go/transport/manager.go and the real selection path of the C# TransportManager. Holds its
// transports sorted ascending by power_cost_relative and falls through them on send, so an
// expensive last-resort transport (the circuit relay, cost 90) is only reached after every cheaper
// one declines. Re-raises every inbound delivery through a single callback tagged with the name of
// the carrying transport.

#define _POSIX_C_SOURCE 200809L

#include "aethernet/transport_manager.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

// Per-transport receive slot. The manager installs relay_slot_on_data() as each transport's
// data-received callback with the slot as user_data, so the trampoline knows which manager to
// notify AND which transport name to tag the delivery with — the C equivalent of the Go per-
// transport closure that captures `via`.
typedef struct {
    aethernet_transport_manager_t *mgr;
    aethernet_transport_t *transport;   // borrowed
    const char *via;                    // borrowed: the transport's vtable name (stable pointer)
} manager_slot_t;

struct aethernet_transport_manager {
    manager_slot_t *slots;   // one per managed transport, ordered ascending by power cost
    size_t count;

    pthread_mutex_t mu;      // guards on_data / on_data_ud (read on transports' delivery threads)
    aethernet_transport_manager_on_data_fn on_data;
    void *on_data_ud;
};

// ── inbound trampoline ──────────────────────────────────────────────────────

static void manager_slot_on_data(const char *sender, const uint8_t *data,
                                 size_t data_len, void *user_data) {
    manager_slot_t *slot = (manager_slot_t *)user_data;
    if (!slot || !slot->mgr) return;
    aethernet_transport_manager_t *mgr = slot->mgr;

    pthread_mutex_lock(&mgr->mu);
    aethernet_transport_manager_on_data_fn cb = mgr->on_data;
    void *ud = mgr->on_data_ud;
    pthread_mutex_unlock(&mgr->mu);

    if (cb) cb(sender, data, data_len, slot->via, ud);
}

// ── ordering ────────────────────────────────────────────────────────────────

// Static performance characteristic used for ordering. Absent a vtable (defensive), treat as the
// most expensive so it sorts last.
static int32_t transport_power_cost(const aethernet_transport_t *t) {
    if (!t || !t->vtable) return INT32_MAX;
    return t->vtable->power_cost_relative;
}

// STABLE ascending insertion sort by power cost over the slot array. Equal costs keep their
// original (registration) order, matching the C# OrderBy stable sort and Go SliceStable. n is tiny
// (a handful of transports), so insertion sort is both simplest and stable.
static void sort_slots_by_power_cost(manager_slot_t *slots, size_t n) {
    for (size_t i = 1; i < n; i++) {
        manager_slot_t key = slots[i];
        int32_t key_cost = transport_power_cost(key.transport);
        size_t j = i;
        while (j > 0 && transport_power_cost(slots[j - 1].transport) > key_cost) {
            slots[j] = slots[j - 1];
            j--;
        }
        slots[j] = key;
    }
}

// ── lifecycle ───────────────────────────────────────────────────────────────

aethernet_transport_manager_t *aethernet_transport_manager_new(
    aethernet_transport_t *const *transports, size_t count) {
    if (count > 0 && !transports) return NULL;

    aethernet_transport_manager_t *mgr =
        (aethernet_transport_manager_t *)calloc(1, sizeof(*mgr));
    if (!mgr) return NULL;

    if (pthread_mutex_init(&mgr->mu, NULL) != 0) { free(mgr); return NULL; }

    if (count > 0) {
        mgr->slots = (manager_slot_t *)calloc(count, sizeof(manager_slot_t));
        if (!mgr->slots) {
            pthread_mutex_destroy(&mgr->mu);
            free(mgr);
            return NULL;
        }
        // Copy non-NULL transports into slots.
        size_t k = 0;
        for (size_t i = 0; i < count; i++) {
            aethernet_transport_t *t = transports[i];
            if (!t) continue; // skip NULL elements
            mgr->slots[k].mgr = mgr;
            mgr->slots[k].transport = t;
            mgr->slots[k].via = (t->vtable ? t->vtable->name : NULL);
            k++;
        }
        mgr->count = k;

        // Order ascending by power cost (stable), then subscribe to each transport's receive
        // surface so inbound data re-raises through the manager tagged with that transport's name.
        sort_slots_by_power_cost(mgr->slots, mgr->count);
        for (size_t i = 0; i < mgr->count; i++) {
            aethernet_transport_set_on_data_received(mgr->slots[i].transport,
                                                     manager_slot_on_data, &mgr->slots[i]);
        }
    }

    return mgr;
}

void aethernet_transport_manager_destroy(aethernet_transport_manager_t *mgr) {
    if (!mgr) return;
    // Detach our trampoline from each managed transport so a later delivery can't touch freed slots.
    for (size_t i = 0; i < mgr->count; i++) {
        aethernet_transport_set_on_data_received(mgr->slots[i].transport, NULL, NULL);
    }
    pthread_mutex_destroy(&mgr->mu);
    free(mgr->slots);
    free(mgr);
}

// ── receive ─────────────────────────────────────────────────────────────────

void aethernet_transport_manager_set_on_data(aethernet_transport_manager_t *mgr,
                                             aethernet_transport_manager_on_data_fn cb,
                                             void *user_data) {
    if (!mgr) return;
    pthread_mutex_lock(&mgr->mu);
    mgr->on_data = cb;
    mgr->on_data_ud = user_data;
    pthread_mutex_unlock(&mgr->mu);
}

// ── send ────────────────────────────────────────────────────────────────────

bool aethernet_transport_manager_send(aethernet_transport_manager_t *mgr,
                                     const char *peer_uhid,
                                     const uint8_t *data, size_t data_len) {
    if (!mgr || !peer_uhid || !data) return false;
    // Ascending power-cost order: try the cheapest first, fall through to the relay (cost 90) last.
    for (size_t i = 0; i < mgr->count; i++) {
        if (aethernet_transport_send(mgr->slots[i].transport, peer_uhid, data, data_len)) {
            return true;
        }
    }
    return false;
}

// ── introspection ───────────────────────────────────────────────────────────

size_t aethernet_transport_manager_count(const aethernet_transport_manager_t *mgr) {
    return mgr ? mgr->count : 0;
}

aethernet_transport_t *aethernet_transport_manager_at(const aethernet_transport_manager_t *mgr,
                                                     size_t i) {
    if (!mgr || i >= mgr->count) return NULL;
    return mgr->slots[i].transport;
}
