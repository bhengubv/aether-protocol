// SPDX-License-Identifier: MIT
// In-Process Transport Implementation (for testing)

#include <stdlib.h>
#include <string.h>
#include <pthread.h>

#include "aethernet/transport.h"

#define AETHERNET_MAX_NODES_INPROCESS 256

/**
 * In-process node entry.
 */
typedef struct {
    char uhid[128];
    aethernet_transport_on_data_received callback;
    void *user_data;
} aethernet_inprocess_node_t;

/**
 * In-process transport state.
 */
typedef struct aethernet_inprocess_transport {
    aethernet_inprocess_node_t nodes[AETHERNET_MAX_NODES_INPROCESS];
    int node_count;
    pthread_mutex_t lock;
} aethernet_inprocess_transport_t;

/**
 * Find a node by UHID.
 */
static int find_node_index(aethernet_inprocess_transport_t *state, const char *uhid) {
    for (int i = 0; i < state->node_count; i++) {
        if (strcmp(state->nodes[i].uhid, uhid) == 0) {
            return i;
        }
    }
    return -1;
}

/**
 * Send implementation.
 */
static bool inprocess_send(void *handle,
                          const char *peer_uhid,
                          const uint8_t *data,
                          size_t data_len) {
    if (!handle || !peer_uhid || !data) return false;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)handle;
    pthread_mutex_lock(&state->lock);

    int peer_idx = find_node_index(state, peer_uhid);
    if (peer_idx < 0) {
        pthread_mutex_unlock(&state->lock);
        return false;
    }

    aethernet_inprocess_node_t *peer = &state->nodes[peer_idx];
    if (!peer->callback) {
        pthread_mutex_unlock(&state->lock);
        return false;
    }

    // Make a copy of the callback and user_data while holding lock
    aethernet_transport_on_data_received callback = peer->callback;
    void *user_data = peer->user_data;

    // Make a copy of the UHID for the callback (which sender should be?)
    // In a real scenario, the sender's UHID would be encoded in the packet
    // For now, we don't have that information, so we pass an empty string
    char sender_uhid[128] = {0};

    pthread_mutex_unlock(&state->lock);

    // Call the callback outside the lock to avoid deadlock
    if (callback) {
        callback(sender_uhid, data, data_len, user_data);
    }

    return true;
}

/**
 * Is connected implementation.
 */
static bool inprocess_is_connected(void *handle, const char *peer_uhid) {
    if (!handle || !peer_uhid) return false;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)handle;
    pthread_mutex_lock(&state->lock);

    bool found = find_node_index(state, peer_uhid) >= 0;

    pthread_mutex_unlock(&state->lock);

    return found;
}

/**
 * Set data received callback.
 */
static void inprocess_set_on_data_received(void *handle,
                                          aethernet_transport_on_data_received callback,
                                          void *user_data) {
    if (!handle) return;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)handle;
    pthread_mutex_lock(&state->lock);

    // This is a bit tricky: we're setting the global callback for a specific node
    // In a more complete implementation, we'd need to know which node this is for
    // For now, we'll skip this

    pthread_mutex_unlock(&state->lock);
}

/**
 * Destroy transport.
 */
static void inprocess_destroy(void *handle) {
    if (!handle) return;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)handle;
    pthread_mutex_destroy(&state->lock);
    free(state);
}

/**
 * Create in-process transport.
 */
aethernet_transport_t *aethernet_inprocess_transport_new(void) {
    aethernet_transport_t *transport = (aethernet_transport_t *)malloc(sizeof(aethernet_transport_t));
    if (!transport) return NULL;

    aethernet_inprocess_transport_t *state =
        (aethernet_inprocess_transport_t *)malloc(sizeof(aethernet_inprocess_transport_t));
    if (!state) {
        free(transport);
        return NULL;
    }

    memset(state, 0, sizeof(aethernet_inprocess_transport_t));
    pthread_mutex_init(&state->lock, NULL);

    aethernet_transport_vtable_t *vtable =
        (aethernet_transport_vtable_t *)malloc(sizeof(aethernet_transport_vtable_t));
    if (!vtable) {
        free(state);
        free(transport);
        return NULL;
    }

    vtable->name = "inprocess";
    vtable->send = inprocess_send;
    vtable->is_connected = inprocess_is_connected;
    vtable->set_on_data_received = inprocess_set_on_data_received;
    vtable->destroy = inprocess_destroy;

    transport->vtable = vtable;
    transport->handle = state;

    return transport;
}

/**
 * Register a node with the in-process transport.
 */
bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport,
                                              const char *uhid) {
    if (!transport || !uhid) return false;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)transport->handle;
    if (!state) return false;

    pthread_mutex_lock(&state->lock);

    if (state->node_count >= AETHERNET_MAX_NODES_INPROCESS) {
        pthread_mutex_unlock(&state->lock);
        return false;
    }

    // Check if already registered
    if (find_node_index(state, uhid) >= 0) {
        pthread_mutex_unlock(&state->lock);
        return false;
    }

    aethernet_inprocess_node_t *node = &state->nodes[state->node_count];
    strncpy(node->uhid, uhid, sizeof(node->uhid) - 1);
    node->uhid[sizeof(node->uhid) - 1] = '\0';
    node->callback = NULL;
    node->user_data = NULL;

    state->node_count++;

    pthread_mutex_unlock(&state->lock);

    return true;
}

/**
 * Unregister a node from the in-process transport.
 */
bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport,
                                                 const char *uhid) {
    if (!transport || !uhid) return false;

    aethernet_inprocess_transport_t *state = (aethernet_inprocess_transport_t *)transport->handle;
    if (!state) return false;

    pthread_mutex_lock(&state->lock);

    int idx = find_node_index(state, uhid);
    if (idx < 0) {
        pthread_mutex_unlock(&state->lock);
        return false;
    }

    // Remove by shifting
    for (int i = idx; i < state->node_count - 1; i++) {
        state->nodes[i] = state->nodes[i + 1];
    }
    state->node_count--;

    pthread_mutex_unlock(&state->lock);

    return true;
}

/**
 * Generic transport functions.
 */

bool aethernet_transport_send(aethernet_transport_t *transport,
                          const char *peer_uhid,
                          const uint8_t *data,
                          size_t data_len) {
    if (!transport || !transport->vtable || !transport->vtable->send) return false;
    return transport->vtable->send(transport->handle, peer_uhid, data, data_len);
}

bool aethernet_transport_is_connected(aethernet_transport_t *transport,
                                  const char *peer_uhid) {
    if (!transport || !transport->vtable || !transport->vtable->is_connected) return false;
    return transport->vtable->is_connected(transport->handle, peer_uhid);
}

void aethernet_transport_set_on_data_received(aethernet_transport_t *transport,
                                          aethernet_transport_on_data_received callback,
                                          void *user_data) {
    if (!transport || !transport->vtable || !transport->vtable->set_on_data_received) return;
    transport->vtable->set_on_data_received(transport->handle, callback, user_data);
}

void aethernet_transport_destroy(aethernet_transport_t *transport) {
    if (!transport) return;

    if (transport->vtable && transport->vtable->destroy) {
        transport->vtable->destroy(transport->handle);
    }

    if (transport->vtable) {
        free(transport->vtable);
    }

    free(transport);
}
