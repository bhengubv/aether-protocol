// SPDX-License-Identifier: MIT
// Aether DirectoryService — application-layer name -> ContentDescriptor resolver.
//
// Closes the Wave-16 protocol gap (Issue #60): IContentService is content-addressed,
// i.e. keyed by rootHash. Consumers that want to fetch content by an
// application-layer name (e.g. "podcast:abc123", "album:artist/title") cannot use
// IContentService alone because they do not know the rootHash upfront — that is
// precisely what they are trying to discover.
//
// This service maintains a local name -> descriptor catalogue, broadcasts
// PacketType.NamePublish when the local node publishes a binding, emits
// PacketType.NameQuery when the local node needs to resolve an unknown name,
// and unicasts a PacketType.NamePublish response when a peer's query matches an
// entry we hold.
//
// Wire payloads are JSON with snake_case field names — byte-identical across
// languages so a C node can answer a query from a C# node and vice versa.
//
// Added in protocol v1.2.0. Mirrors AetherNet.Content.DirectoryService (C# reference).

#ifndef AETHERNET_DIRECTORY_SERVICE_H
#define AETHERNET_DIRECTORY_SERVICE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── ContentDescriptor (mirrors AetherNet.Content.Models.ContentDescriptor) ───
//
// Heap-allocated; populate via aethernet_content_descriptor_new() / free via
// aethernet_content_descriptor_free(). All string fields are owned (deep-copied
// on set). chunk_hashes is an array of owned C strings; chunk_hashes_count holds
// the length.
//
// snake_case JSON wire shape:
//   {
//     "root_hash":"<hex>",
//     "name":"<str>",
//     "total_bytes":<int64>,
//     "chunk_size_bytes":<int32>,
//     "chunk_count":<int32>,
//     "chunk_hashes":["<hex>", ...],
//     "content_type":"<mime>",
//     "created_at":"<ISO-8601>"
//   }
typedef struct {
    char    *root_hash;          // owned, hex lowercase SHA-256
    char    *name;               // owned, publisher-provided file/object name (hint)
    int64_t  total_bytes;        // total content size
    int32_t  chunk_size_bytes;   // bytes per chunk (last chunk may be smaller)
    int32_t  chunk_count;        // = ceil(total_bytes / chunk_size_bytes)
    char   **chunk_hashes;       // owned array of owned hex strings
    int32_t  chunk_hashes_count; // length of chunk_hashes
    char    *content_type;       // owned, MIME-type hint
    char    *created_at;         // owned, ISO-8601 UTC timestamp string
} aethernet_content_descriptor_t;

/// Allocate a zero-initialised descriptor. Caller fills fields, frees via aethernet_content_descriptor_free().
aethernet_content_descriptor_t *aethernet_content_descriptor_new(void);

/// Deep-free a descriptor including every owned string and the chunk_hashes array.
void aethernet_content_descriptor_free(aethernet_content_descriptor_t *desc);

/// Deep-copy a descriptor (independent allocations for every owned string).
/// Returns a heap-allocated clone or NULL on OOM. Caller frees via
/// aethernet_content_descriptor_free().
aethernet_content_descriptor_t *aethernet_content_descriptor_clone(const aethernet_content_descriptor_t *src);


// ─── DirectoryService ────────────────────────────────────

/**
 * Entry-announced callback fired when a NamePublish packet arrives and either
 * registers a new name in the local catalogue or replaces an existing one.
 *
 * Arguments:
 *   user_data    — pass-through cookie supplied at registration
 *   name         — the newly-announced application-layer name (NUL-terminated)
 *   descriptor   — borrowed pointer; valid only inside the callback
 *   source_uhid  — UHID of the peer that emitted the publish
 *
 * The DirectoryService retains ownership of `descriptor` — callers that need
 * the descriptor outside the callback must clone it via
 * aethernet_content_descriptor_clone().
 */
typedef void (*aethernet_directory_entry_announced_cb)(
    void *user_data,
    const char *name,
    const aethernet_content_descriptor_t *descriptor,
    const char *source_uhid
);

/// Opaque DirectoryService handle.
typedef struct aethernet_directory_service aethernet_directory_service_t;

/**
 * Create a DirectoryService. Borrows `sender` — caller must keep it alive for
 * the service lifetime. Returns NULL on OOM or NULL sender.
 *
 * `local_uhid` is captured from `sender->local_uhid` at construction; the
 * caller keeps ownership of the underlying string (consistent with how
 * aethernet_routing_service / aethernet_dtn_service handle sender ownership).
 */
int aethernet_directory_service_init(
    aethernet_directory_service_t **out_svc,
    aethernet_mesh_sender_t *sender
);

/// Free the service and its in-memory catalogue.
void aethernet_directory_service_free(aethernet_directory_service_t *svc);

/**
 * Store the binding locally and broadcast a NamePublish packet.
 * Returns 0 on success, -1 on error.
 *
 * The catalogue takes a deep copy of `descriptor` — the caller retains
 * ownership of its argument.
 */
int aethernet_directory_publish(
    aethernet_directory_service_t *svc,
    const char *name,
    const aethernet_content_descriptor_t *descriptor
);

/**
 * Resolve a name to its descriptor.
 *
 * Returns:
 *    0  — local-catalogue hit; `out_descriptor` is filled with a deep-copy
 *         the caller must free via aethernet_content_descriptor_free().
 *    1  — no local hit; a NameQuery has been broadcast. Caller wires
 *         aethernet_directory_handle() for inbound NamePublish responses and
 *         polls the local catalogue afterwards. (`out_descriptor` left
 *         untouched.) This C reference impl is single-threaded and does not
 *         block on a condition variable; hosts that need blocking-resolve
 *         wrap this in a thread + condition primitive on their side.
 *   -1  — error (NULL args / OOM / broadcast failed).
 *
 * `timeout_seconds` is accepted for API symmetry with the C# reference but is
 * not honoured by this single-threaded impl (caller drives the polling cadence).
 */
int aethernet_directory_resolve(
    aethernet_directory_service_t *svc,
    const char *name,
    aethernet_content_descriptor_t *out_descriptor,
    int timeout_seconds
);

/**
 * Pump an inbound NamePublish / NameQuery packet. Non-directory packet types
 * are silently ignored. Returns 0 on success, -1 on parse / OOM error.
 */
int aethernet_directory_handle(
    aethernet_directory_service_t *svc,
    const aethernet_mesh_packet_t *packet
);

/**
 * Register an entry-announced callback. Pass cb=NULL to detach. `user_data` is
 * passed back verbatim — caller owns it.
 */
void aethernet_directory_set_entry_announced_callback(
    aethernet_directory_service_t *svc,
    aethernet_directory_entry_announced_cb cb,
    void *user_data
);

/**
 * Snapshot the catalogue. Returns the number of entries currently held; the
 * caller-allocated `names_out` array (size = `max_names`) is filled with
 * pointers BORROWED from the service (valid until the next publish / handle).
 *
 * Pass NULL for `names_out` to query the count only.
 */
int aethernet_directory_list_names(
    aethernet_directory_service_t *svc,
    const char **names_out,
    int max_names
);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_DIRECTORY_SERVICE_H
