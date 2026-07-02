// SPDX-License-Identifier: MIT
// aether-space breadcrumb WIRE binding — SpaceBreadcrumb (PacketType 40) transport.
//
// Thin mesh binding over the aether-space extension: broadcast a locally-dropped breadcrumb to the
// whole mesh (source -> "*"), and surface inbound breadcrumbs via a callback (the host pins them into
// its aethernet_space_service). Transport only — no store, no re-host policy (that lives in space.c /
// AetherNet.Space's ISpaceService). Mirrors the green C# SpaceBreadcrumbService.
//
// The wire payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field
// order content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature; created_at_ms
// bare int64 Unix ms; ttl_hours + type bare ints; signature STANDARD base64 of the raw bytes, "" when
// empty) and decoded on receive with the vendored cJSON, matching the prekey / sos / channels approach.
// Byte-identity gate: fixtures/space/vectors.json.
//
// The event-args type is the existing aethernet_space_breadcrumb_t from <aethernet/space.h> — reused
// so a host can hand a received breadcrumb straight to aethernet_space_pin without a translation step.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service in
// their own mutex (matches sos.c / channels.c / videocall.c / prekey.c).

#ifndef AETHERNET_SPACE_BREADCRUMB_H
#define AETHERNET_SPACE_BREADCRUMB_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet/space.h"   // aethernet_space_breadcrumb_t (reused as the received-event args)

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Serialize a SpaceBreadcrumb (PacketType 40) payload to canonical UTF-8 JSON. Field order:
 *   content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature
 * No whitespace; created_at_ms is a bare int64 (Unix ms); ttl_hours + type are bare ints; signature is
 * STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding) of the raw signature bytes, or the empty
 * string "" when the breadcrumb is unsigned (signature NULL or signature_len 0). Cross-language
 * byte-identity gate (fixtures/space/vectors.json). On success writes a heap-allocated buffer to
 * *out_json (null-terminated just past *out_len) and its length to *out_len, returns true. Caller owns
 * *out_json and frees it with free(). Returns false on allocation failure or a NULL argument.
 */
bool aethernet_space_breadcrumb_payload_serialize(const aethernet_space_breadcrumb_t *breadcrumb,
                                                  uint8_t **out_json,
                                                  uint32_t *out_len);

/**
 * A received breadcrumb surfaced to the host when a SpaceBreadcrumb arrives. The breadcrumb is borrowed
 * for the callback duration — copy anything you wish to retain (or hand it to aethernet_space_pin,
 * which copies). Mirrors the C# BreadcrumbReceived event payload.
 */
typedef void (*aethernet_space_breadcrumb_received_cb)(const aethernet_space_breadcrumb_t *breadcrumb,
                                                       void *user_data);

/**
 * Opaque SpaceBreadcrumb wire-binding handle. Broadcasts SpaceBreadcrumb packets and surfaces inbound
 * breadcrumbs via the received callback. The service borrows `sender` — caller keeps it alive for the
 * service lifetime.
 */
typedef struct aethernet_space_breadcrumb_service aethernet_space_breadcrumb_service_t;

aethernet_space_breadcrumb_service_t *aethernet_space_breadcrumb_service_new(aethernet_mesh_sender_t *sender);
void aethernet_space_breadcrumb_service_free(aethernet_space_breadcrumb_service_t *service);

/**
 * Set the breadcrumb-received callback (fired on each inbound SpaceBreadcrumb). Pass NULL to clear.
 * Mirrors wiring the C# BreadcrumbReceived event handler.
 */
void aethernet_space_breadcrumb_set_received_cb(aethernet_space_breadcrumb_service_t *service,
                                                aethernet_space_breadcrumb_received_cb cb,
                                                void *user_data);

/**
 * Flood a breadcrumb to mesh peers: build a SpaceBreadcrumb packet (source local UHID, dest "*", TTL
 * AETHERNET_DEFAULT_TTL) carrying the canonical JSON payload and broadcast it via sender->broadcast.
 * On success writes the fan-out count (peers reached) to *out_count if non-NULL and returns true.
 * Returns false if `service`/`breadcrumb` is NULL, the payload fails to encode, or the host wired no
 * broadcast. Mirrors the C# BroadcastAsync.
 */
bool aethernet_space_breadcrumb_broadcast(aethernet_space_breadcrumb_service_t *service,
                                          const aethernet_space_breadcrumb_t *breadcrumb,
                                          int *out_count);

/**
 * Process an inbound SpaceBreadcrumb packet: decode the payload and fire the received callback with the
 * decoded breadcrumb, returning true. Returns false for the wrong packet type, a malformed payload, an
 * empty content_hash (mirrors the C# string.IsNullOrEmpty(ContentHash) guard), or a NULL argument.
 * Mirrors the C# HandleAsync.
 */
bool aethernet_space_breadcrumb_handle_packet(aethernet_space_breadcrumb_service_t *service,
                                              const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_SPACE_BREADCRUMB_H
