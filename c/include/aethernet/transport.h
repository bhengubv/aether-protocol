// SPDX-License-Identifier: MIT
// Aether Transport Layer - Abstract Interface and In-Process Transport

#ifndef AETHERNET_TRANSPORT_H
#define AETHERNET_TRANSPORT_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Transport callback type: invoked when data is received from a peer.
 */
typedef void (*aethernet_transport_on_data_received)(
    const char *sender_uhid,
    const uint8_t *data,
    size_t data_len,
    void *user_data
);

/**
 * Abstract transport vtable.
 * Each transport implementation (BLE, Wi-Fi Direct, NearLink, in-process) provides these methods.
 */
typedef struct {
    // Human-readable name (e.g., "BLE", "WiFi-Direct")
    const char *name;

    // Send a byte array to a specific peer
    // Returns: true on success
    bool (*send)(void *transport_handle,
                 const char *peer_uhid,
                 const uint8_t *data,
                 size_t data_len);

    // Check if a connection is active to a peer
    bool (*is_connected)(void *transport_handle,
                         const char *peer_uhid);

    // Register a data received callback
    void (*set_on_data_received)(void *transport_handle,
                                 aethernet_transport_on_data_received callback,
                                 void *user_data);

    // Clean up transport resources
    void (*destroy)(void *transport_handle);

} aethernet_transport_vtable_t;

/**
 * Transport instance wrapper.
 */
typedef struct {
    aethernet_transport_vtable_t *vtable;
    void *handle;  // Opaque pointer to transport-specific state
} aethernet_transport_t;

/**
 * In-process transport: allows communication between multiple nodes within
 * the same process, useful for testing and embedded scenarios.
 * Internally maintains a static array of registered nodes and uses
 * mutex-protected delivery.
 */
typedef struct aethernet_inprocess_transport aethernet_inprocess_transport_t;

/**
 * Create an in-process transport.
 * This creates a shared transport that can connect multiple nodes
 * running in the same process.
 *
 * Returns: allocated transport, or NULL on error.
 * Caller must free with aethernet_transport_destroy().
 */
aethernet_transport_t *aethernet_inprocess_transport_new(void);

/**
 * Register a node with the in-process transport.
 * This allows the node to send and receive messages via the transport.
 *
 * Returns: true on success.
 */
bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport,
                                               const char *uhid);

/**
 * Unregister a node from the in-process transport.
 *
 * Returns: true on success.
 */
bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport,
                                                 const char *uhid);

/**
 * Generic transport functions.
 */

/**
 * Send data via a transport.
 *
 * Returns: true on success.
 */
bool aethernet_transport_send(aethernet_transport_t *transport,
                          const char *peer_uhid,
                          const uint8_t *data,
                          size_t data_len);

/**
 * Check if connected to a peer via a transport.
 *
 * Returns: true if connected.
 */
bool aethernet_transport_is_connected(aethernet_transport_t *transport,
                                   const char *peer_uhid);

/**
 * Register a callback for incoming data.
 */
void aethernet_transport_set_on_data_received(aethernet_transport_t *transport,
                                          aethernet_transport_on_data_received callback,
                                          void *user_data);

/**
 * Destroy a transport and free resources.
 */
void aethernet_transport_destroy(aethernet_transport_t *transport);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_TRANSPORT_H
