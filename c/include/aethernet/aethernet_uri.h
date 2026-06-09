// SPDX-License-Identifier: MIT
// AetherNet URI scheme — `aether://` canonical addressing format.
//
// Mirrors the C# reference implementation under
// src/AetherNet.Core/Uri/. The wire form, percent-encoding rules,
// authority canonicalisation and dispatch semantics are byte-equal
// across all 8 SDKs. See docs/aether-uri-scheme.md for the grammar
// and tests/cross-language/uri-fixtures.json for the conformance
// corpus.
//
// ─── Grammar (ABNF, RFC 5234) ────────────────────────────────────────────
//   aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
//   authority    = aether-tag / uhid
//   aether-tag   = 5(crockford) [ "-" ] 5(crockford)        ; case-insensitive
//   uhid         = 64(HEXDIG)                                ; SHA-256 hex of pubkey
//   path         = path-segment *( "/" path-segment )
//   query        = key [ "=" value ] *( "&" key [ "=" value ] )
//
// ─── Memory-management contract ──────────────────────────────────────────
// Every `_new()` / `_parse()` / `_to_string()` / `_builder_build()` allocates
// from the heap. The caller frees:
//
//   aethernet_uri_t                    -> aethernet_uri_free()
//   aethernet_uri_builder_t            -> aethernet_uri_builder_free()
//   aethernet_uri_handler_descriptor_t -> aethernet_uri_handler_descriptor_free()
//   aethernet_uri_handler_manifest_t   -> aethernet_uri_handler_manifest_free()
//                                          (also frees every descriptor added to it
//                                           — see _manifest_add() below for ownership transfer)
//   aethernet_uri_router_t             -> aethernet_uri_router_free()
//   char * (from _to_string())         -> aethernet_uri_free_string()
//
// Getters that return `const char *` borrow from the parent struct — the
// pointer is valid for the lifetime of the parent and must NOT be freed.

#ifndef AETHERNET_URI_H
#define AETHERNET_URI_H

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/** The fixed scheme name. Lower-case on emit, case-insensitive on parse. */
#define AETHERNET_URI_SCHEME        "aether"
#define AETHERNET_URI_SCHEME_PREFIX "aether://"

typedef struct aethernet_uri                    aethernet_uri_t;
typedef struct aethernet_uri_builder            aethernet_uri_builder_t;
typedef struct aethernet_uri_handler_descriptor aethernet_uri_handler_descriptor_t;
typedef struct aethernet_uri_handler_manifest   aethernet_uri_handler_manifest_t;
typedef struct aethernet_uri_router             aethernet_uri_router_t;

/* ── Parse / try-parse ──────────────────────────────────────────────────── */

/**
 * Parse an aether:// URI.
 *
 * Parameters:
 *   input          — NUL-terminated input string (must not be NULL).
 *   error_out      — optional caller-allocated buffer for an error message.
 *                    Pass NULL to discard. On failure the buffer is filled
 *                    with a NUL-terminated diagnostic.
 *   error_out_size — capacity of error_out in bytes (ignored if NULL).
 *
 * Returns:  newly-allocated aethernet_uri_t* on success — caller frees with
 *           aethernet_uri_free().
 *           NULL on any syntactic violation.
 */
aethernet_uri_t *aethernet_uri_parse(const char *input,
                                     char       *error_out,
                                     size_t      error_out_size);

/** Frees a URI produced by aethernet_uri_parse() or aethernet_uri_builder_build(). */
void aethernet_uri_free(aethernet_uri_t *uri);

/* ── Getters (returned pointers borrow uri's lifetime) ──────────────────── */

/** Canonicalised authority (upper-case, dashed for AetherTags). */
const char *aethernet_uri_authority(const aethernet_uri_t *uri);

/** Decoded path without leading slash. Empty string for root. */
const char *aethernet_uri_path(const aethernet_uri_t *uri);

/** Decoded fragment without leading '#'. Empty string if absent. */
const char *aethernet_uri_fragment(const aethernet_uri_t *uri);

/** First path segment (the handler name). Empty for root. */
const char *aethernet_uri_handler_name(const aethernet_uri_t *uri);

/** Number of slash-separated, percent-decoded segments in the path. */
size_t aethernet_uri_path_segment_count(const aethernet_uri_t *uri);

/** Returns segment[index] or NULL when index is out of range. */
const char *aethernet_uri_path_segment(const aethernet_uri_t *uri, size_t index);

/* ── Query ──────────────────────────────────────────────────────────────── */

/** Number of distinct query keys present. */
size_t aethernet_uri_query_count(const aethernet_uri_t *uri);

/**
 * Look up a query value by key. The key is compared case-insensitively.
 * Returns NULL when the key is absent. An empty value (e.g. "?flag") yields
 * a pointer to a NUL byte, not NULL.
 */
const char *aethernet_uri_query_get(const aethernet_uri_t *uri, const char *key);

/** Returns the lower-cased key at index, or NULL when out of range. */
const char *aethernet_uri_query_key_at(const aethernet_uri_t *uri, size_t index);

/** Returns the value at index, or NULL when out of range. */
const char *aethernet_uri_query_value_at(const aethernet_uri_t *uri, size_t index);

/* ── Canonical serialisation ────────────────────────────────────────────── */

/**
 * Render the URI to its canonical, RFC-safe string form.
 *
 * Returns a freshly heap-allocated NUL-terminated string. Caller frees with
 * aethernet_uri_free_string(). NULL on allocation failure or invalid URI.
 */
char *aethernet_uri_to_string(const aethernet_uri_t *uri);

/** Frees a string produced by aethernet_uri_to_string(). */
void aethernet_uri_free_string(char *s);

/* ── Equality ───────────────────────────────────────────────────────────── */

/**
 * Structural equality: authority + path + fragment + query map.
 * Query comparison is order-insensitive and key-case-insensitive.
 * NULL operands compare equal only to each other.
 */
bool aethernet_uri_equals(const aethernet_uri_t *a, const aethernet_uri_t *b);

/* ── Builder ────────────────────────────────────────────────────────────── */

/** Allocate a new builder. Free with aethernet_uri_builder_free(). */
aethernet_uri_builder_t *aethernet_uri_builder_new(void);

void aethernet_uri_builder_free(aethernet_uri_builder_t *b);

/** Sets the authority. The string is validated when build() is called. */
void aethernet_uri_builder_authority(aethernet_uri_builder_t *b, const char *authority);

/** Replaces the path (any leading '/' is stripped). */
void aethernet_uri_builder_path(aethernet_uri_builder_t *b, const char *path);

/** Appends a single segment, inserting a '/' separator as needed. */
void aethernet_uri_builder_append_segment(aethernet_uri_builder_t *b, const char *segment);

/** Sets a query key=value. Replaces any existing value for the same key. */
void aethernet_uri_builder_query(aethernet_uri_builder_t *b,
                                 const char *key,
                                 const char *value);

/** Removes a query key if present. */
void aethernet_uri_builder_remove_query(aethernet_uri_builder_t *b, const char *key);

/** Sets the fragment (any leading '#' is stripped). */
void aethernet_uri_builder_fragment(aethernet_uri_builder_t *b, const char *fragment);

/**
 * Builds the final URI by serialising the builder and re-parsing it. This
 * guarantees the result is canonicalised + validated. Returns NULL on
 * failure; if error_out is non-NULL the buffer is filled with a diagnostic.
 */
aethernet_uri_t *aethernet_uri_builder_build(aethernet_uri_builder_t *b,
                                             char  *error_out,
                                             size_t error_out_size);

/* ── Handler manifest ───────────────────────────────────────────────────── */

/**
 * Allocate a descriptor.
 *
 * name          — first path segment to match (required, must not be empty).
 * path_template — template after the name. May be NULL or "" for a root
 *                 handler. Placeholders use {captureName} syntax.
 * description   — optional free-text description.
 *
 * Returns NULL on allocation failure or when name is NULL/empty.
 * Caller frees with aethernet_uri_handler_descriptor_free().
 */
aethernet_uri_handler_descriptor_t *aethernet_uri_handler_descriptor_new(
    const char *name,
    const char *path_template,
    const char *description);

void aethernet_uri_handler_descriptor_free(aethernet_uri_handler_descriptor_t *d);

/* Read accessors (borrow descriptor lifetime). */
const char *aethernet_uri_handler_descriptor_name(const aethernet_uri_handler_descriptor_t *d);
const char *aethernet_uri_handler_descriptor_template(const aethernet_uri_handler_descriptor_t *d);
const char *aethernet_uri_handler_descriptor_description(const aethernet_uri_handler_descriptor_t *d);

/**
 * Allocate a manifest for the given app id. Returns NULL on bad input.
 * Caller frees with aethernet_uri_handler_manifest_free().
 */
aethernet_uri_handler_manifest_t *aethernet_uri_handler_manifest_new(const char *app_id);

/**
 * Add a descriptor to the manifest. Ownership of `d` transfers to the
 * manifest — do NOT call aethernet_uri_handler_descriptor_free(d) afterwards.
 * Returns 0 on success, -1 on bad input or allocation failure.
 */
int aethernet_uri_handler_manifest_add(aethernet_uri_handler_manifest_t   *m,
                                       aethernet_uri_handler_descriptor_t *d);

/** Frees the manifest and every descriptor it owns. */
void aethernet_uri_handler_manifest_free(aethernet_uri_handler_manifest_t *m);

/** Manifest read accessors. */
const char *aethernet_uri_handler_manifest_app_id(const aethernet_uri_handler_manifest_t *m);
size_t      aethernet_uri_handler_manifest_count(const aethernet_uri_handler_manifest_t *m);
const aethernet_uri_handler_descriptor_t *aethernet_uri_handler_manifest_at(
    const aethernet_uri_handler_manifest_t *m, size_t index);

/**
 * Resolve an incoming URI against the manifest.
 *
 * Walks the manifest in registration order and returns the index of the
 * first descriptor whose name matches uri's handler_name() AND whose
 * template matches the remaining path segments.
 *
 * capture_keys_out / capture_values_out  — caller-allocated arrays of at
 *     least capture_cap pointers each. On a successful match they receive
 *     borrowed pointers into the descriptor's template (key) and uri's path
 *     (value). The pointers are valid for the joint lifetime of the manifest
 *     and uri.
 * captures_out_count                     — out: number of captures written.
 *                                          May be NULL if not needed.
 *
 * Returns the matching handler index, or -1 when no handler matches.
 */
int aethernet_uri_handler_manifest_resolve(
    const aethernet_uri_handler_manifest_t *m,
    const aethernet_uri_t                  *uri,
    const char **capture_keys_out,
    const char **capture_values_out,
    size_t       capture_cap,
    size_t      *captures_out_count);

/* ── Router ─────────────────────────────────────────────────────────────── */

/**
 * Callback invoked by the router for a matched handler.
 *
 * uri              — the dispatched URI (borrowed; valid for the call).
 * handler          — the matched manifest descriptor (borrowed; valid for the call).
 * capture_keys     — parallel array of length capture_count.
 * capture_values   — parallel array of length capture_count.
 * user_data        — the opaque pointer registered with this callback.
 *
 * Return value:    callback's chosen return code; surfaced to the dispatch
 *                  caller. Convention: 0 = handled.
 */
typedef int (*aethernet_uri_handler_callback)(
    const aethernet_uri_t                    *uri,
    const aethernet_uri_handler_descriptor_t *handler,
    const char                              **capture_keys,
    const char                              **capture_values,
    size_t                                    capture_count,
    void                                     *user_data);

/**
 * Allocate a router that resolves against `manifest`. The manifest pointer
 * is borrowed — the caller must keep it alive for the router's lifetime and
 * is responsible for freeing it separately.
 */
aethernet_uri_router_t *aethernet_uri_router_new(
    const aethernet_uri_handler_manifest_t *manifest);

void aethernet_uri_router_free(aethernet_uri_router_t *r);

/**
 * Bind a callback to a handler index. Re-registering the same index
 * replaces the existing callback. Returns 0 on success, -1 on bad inputs.
 * Thread-safe.
 */
int aethernet_uri_router_register(aethernet_uri_router_t        *r,
                                  int                            handler_index,
                                  aethernet_uri_handler_callback cb,
                                  void                          *user_data);

/**
 * Resolve `uri` and invoke the matching callback if any. Returns:
 *   >= 0   — callback's return value when a handler was matched + invoked.
 *   -1     — no handler matched, or no callback was registered for the match.
 *   -2     — uri or router is NULL.
 * Thread-safe.
 */
int aethernet_uri_router_dispatch(aethernet_uri_router_t *r,
                                  const aethernet_uri_t  *uri);

/**
 * Convenience: parse `uri_str` then dispatch. On parse failure, returns -3
 * and fills error_out with a diagnostic when non-NULL.
 */
int aethernet_uri_router_dispatch_string(aethernet_uri_router_t *r,
                                         const char             *uri_str,
                                         char                   *error_out,
                                         size_t                  error_out_size);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_URI_H */
