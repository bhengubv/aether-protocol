// SPDX-License-Identifier: MIT
// In-memory WebRTC signalling bus — routes Signals between endpoints by UHID,
// in process, with no server. The reference Signaling implementation backing
// same-process simulations and the test suite. Mirrors the C#
// InMemoryWebRtcSignalingBus and the Go InMemorySignalingBus.
//
// Each endpoint delivers inbound signals on its own pump thread, in send order,
// so a signal never re-enters the sender's call stack — matching the ordered,
// reliable delivery a real signalling channel provides.

#define _POSIX_C_SOURCE 200809L

#include <stdlib.h>
#include <string.h>
#include <pthread.h>

#include "aethernet/transport_webrtc.h"

#define AETHERNET_WEBRTC_BUS_MAX_ENDPOINTS 256
#define AETHERNET_WEBRTC_BUS_QUEUE_CAP     256

/* ───────────────────────── per-endpoint queue ─────────────────────────────*/

typedef struct {
    aethernet_webrtc_signaling_bus_t *bus;
    char uhid[AETHERNET_WEBRTC_UHID_MAX];

    /* The endpoint exposed to the transport. Its handle points back here. */
    aethernet_webrtc_signaling_t iface;

    /* Bounded ring buffer of queued signals (heap-allocated entries). */
    aethernet_webrtc_signal_t *queue[AETHERNET_WEBRTC_BUS_QUEUE_CAP];
    int   head;
    int   tail;
    int   count;

    aethernet_webrtc_signal_handler handler;
    void                           *handler_user;

    pthread_mutex_t lock;
    pthread_cond_t  not_empty;
    pthread_t       pump;
    bool            running;
    bool            in_use;
} aethernet_webrtc_bus_endpoint_t;

struct aethernet_webrtc_signaling_bus {
    aethernet_webrtc_bus_endpoint_t endpoints[AETHERNET_WEBRTC_BUS_MAX_ENDPOINTS];
    int             endpoint_count;
    pthread_mutex_t lock;   /* guards the endpoints table */
};

/* ───────────────────────── endpoint pump ──────────────────────────────────*/

static void *endpoint_pump(void *arg) {
    aethernet_webrtc_bus_endpoint_t *e = (aethernet_webrtc_bus_endpoint_t *)arg;
    for (;;) {
        pthread_mutex_lock(&e->lock);
        while (e->running && e->count == 0)
            pthread_cond_wait(&e->not_empty, &e->lock);
        if (!e->running && e->count == 0) {
            pthread_mutex_unlock(&e->lock);
            break;
        }
        aethernet_webrtc_signal_t *sig = e->queue[e->head];
        e->head = (e->head + 1) % AETHERNET_WEBRTC_BUS_QUEUE_CAP;
        e->count--;
        aethernet_webrtc_signal_handler h = e->handler;
        void *user = e->handler_user;
        pthread_mutex_unlock(&e->lock);

        if (sig) {
            if (h)
                h(sig, user);   /* runs on the pump thread, never the sender's */
            free(sig);
        }
    }
    return NULL;
}

/* Enqueue a copy of `signal` for delivery on the endpoint's pump thread. */
static void endpoint_deliver(aethernet_webrtc_bus_endpoint_t *e,
                             const aethernet_webrtc_signal_t *signal) {
    aethernet_webrtc_signal_t *copy =
        (aethernet_webrtc_signal_t *)malloc(sizeof(aethernet_webrtc_signal_t));
    if (!copy) return;
    memcpy(copy, signal, sizeof(*copy));

    pthread_mutex_lock(&e->lock);
    if (!e->running || e->count >= AETHERNET_WEBRTC_BUS_QUEUE_CAP) {
        /* queue full or closed — drop; ICE re-gathers on reconnect (best-effort). */
        pthread_mutex_unlock(&e->lock);
        free(copy);
        return;
    }
    e->queue[e->tail] = copy;
    e->tail = (e->tail + 1) % AETHERNET_WEBRTC_BUS_QUEUE_CAP;
    e->count++;
    pthread_cond_signal(&e->not_empty);
    pthread_mutex_unlock(&e->lock);
}

/* ───────────────────────── signalling vtable ──────────────────────────────*/

/* Route a signal to its addressee's endpoint within the same bus. */
static bool bus_iface_send(void *handle, const aethernet_webrtc_signal_t *signal) {
    aethernet_webrtc_bus_endpoint_t *from = (aethernet_webrtc_bus_endpoint_t *)handle;
    if (!from || !signal) return false;
    aethernet_webrtc_signaling_bus_t *bus = from->bus;

    pthread_mutex_lock(&bus->lock);
    aethernet_webrtc_bus_endpoint_t *target = NULL;
    for (int i = 0; i < bus->endpoint_count; i++) {
        if (bus->endpoints[i].in_use &&
            strcmp(bus->endpoints[i].uhid, signal->to_uhid) == 0) {
            target = &bus->endpoints[i];
            break;
        }
    }
    pthread_mutex_unlock(&bus->lock);

    if (!target) return false;  /* no endpoint for to_uhid */
    endpoint_deliver(target, signal);
    return true;
}

static void bus_iface_set_handler(void *handle,
                                  aethernet_webrtc_signal_handler handler,
                                  void *user_data) {
    aethernet_webrtc_bus_endpoint_t *e = (aethernet_webrtc_bus_endpoint_t *)handle;
    if (!e) return;
    pthread_mutex_lock(&e->lock);
    e->handler      = handler;
    e->handler_user = user_data;
    pthread_mutex_unlock(&e->lock);
}

/* ───────────────────────── bus API ────────────────────────────────────────*/

aethernet_webrtc_signaling_bus_t *aethernet_webrtc_signaling_bus_new(void) {
    aethernet_webrtc_signaling_bus_t *bus =
        (aethernet_webrtc_signaling_bus_t *)malloc(sizeof(aethernet_webrtc_signaling_bus_t));
    if (!bus) return NULL;
    memset(bus, 0, sizeof(*bus));
    pthread_mutex_init(&bus->lock, NULL);
    return bus;
}

aethernet_webrtc_signaling_t *
aethernet_webrtc_signaling_bus_endpoint(aethernet_webrtc_signaling_bus_t *bus,
                                        const char *uhid) {
    if (!bus || !uhid || !uhid[0]) return NULL;

    pthread_mutex_lock(&bus->lock);

    /* Return the existing endpoint if one is already registered for this UHID. */
    for (int i = 0; i < bus->endpoint_count; i++) {
        if (bus->endpoints[i].in_use && strcmp(bus->endpoints[i].uhid, uhid) == 0) {
            aethernet_webrtc_signaling_t *iface = &bus->endpoints[i].iface;
            pthread_mutex_unlock(&bus->lock);
            return iface;
        }
    }

    if (bus->endpoint_count >= AETHERNET_WEBRTC_BUS_MAX_ENDPOINTS) {
        pthread_mutex_unlock(&bus->lock);
        return NULL;
    }

    aethernet_webrtc_bus_endpoint_t *e = &bus->endpoints[bus->endpoint_count];
    memset(e, 0, sizeof(*e));
    e->bus = bus;
    strncpy(e->uhid, uhid, sizeof(e->uhid) - 1);
    e->in_use  = true;
    e->running = true;
    pthread_mutex_init(&e->lock, NULL);
    pthread_cond_init(&e->not_empty, NULL);

    e->iface.handle      = e;
    e->iface.send        = bus_iface_send;
    e->iface.set_handler = bus_iface_set_handler;

    if (pthread_create(&e->pump, NULL, endpoint_pump, e) != 0) {
        pthread_cond_destroy(&e->not_empty);
        pthread_mutex_destroy(&e->lock);
        e->in_use  = false;
        e->running = false;
        pthread_mutex_unlock(&bus->lock);
        return NULL;
    }

    bus->endpoint_count++;
    aethernet_webrtc_signaling_t *iface = &e->iface;
    pthread_mutex_unlock(&bus->lock);
    return iface;
}

void aethernet_webrtc_signaling_bus_destroy(aethernet_webrtc_signaling_bus_t *bus) {
    if (!bus) return;

    /* Stop every pump thread, then drain. */
    pthread_mutex_lock(&bus->lock);
    int n = bus->endpoint_count;
    pthread_mutex_unlock(&bus->lock);

    for (int i = 0; i < n; i++) {
        aethernet_webrtc_bus_endpoint_t *e = &bus->endpoints[i];
        if (!e->in_use) continue;
        pthread_mutex_lock(&e->lock);
        e->running = false;
        pthread_cond_signal(&e->not_empty);
        pthread_mutex_unlock(&e->lock);
        pthread_join(e->pump, NULL);

        /* Free any signals left in the queue. */
        while (e->count > 0) {
            free(e->queue[e->head]);
            e->head = (e->head + 1) % AETHERNET_WEBRTC_BUS_QUEUE_CAP;
            e->count--;
        }
        pthread_cond_destroy(&e->not_empty);
        pthread_mutex_destroy(&e->lock);
        e->in_use = false;
    }

    pthread_mutex_destroy(&bus->lock);
    free(bus);
}
