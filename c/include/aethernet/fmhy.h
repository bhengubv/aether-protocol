// SPDX-License-Identifier: MIT
// FMHY content catalogue (Phase-2 extension).
//
// Free Media Heck Yeah (FMHY) directory propagated over the Aether mesh so
// offline peers benefit from entries fetched by connected peers. Port of the C#
// reference (AetherNet.Fmhy): a hand-rolled markdown parser for the FMHY
// single-page dump plus an in-memory catalogue.

#ifndef AETHERNET_FMHY_H
#define AETHERNET_FMHY_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// A single resource parsed from the FMHY directory. Owned string/array fields
/// are freed by aethernet_fmhy_entry_free (for a standalone entry) or by the
/// catalogue service for stored entries.
typedef struct {
    char   *name;          // owned
    char   *url;           // owned
    char   *description;   // owned, may be NULL
    char   *category;      // owned ("H1" or "H1 / H2")
    bool    is_starred;
    char  **mirrors;       // owned array of owned strings
    int32_t mirror_count;
} aethernet_fmhy_entry_t;

/// A known torrent tracker-list aggregator (static; do not free).
typedef struct {
    const char *name;
    const char *url;
    const char *description;
} aethernet_fmhy_tracker_source_t;

/**
 * Parse a raw FMHY markdown string into a newly-allocated array of entries (in
 * document order). Writes the count to *out_count and returns the array (caller
 * frees via aethernet_fmhy_entries_free). Returns NULL with *out_count == 0 when
 * there are no entries.
 */
aethernet_fmhy_entry_t *aethernet_fmhy_parse_markdown(const char *markdown, int32_t *out_count);

/// Free an entry array returned by the parser / browse / get_starred.
void aethernet_fmhy_entries_free(aethernet_fmhy_entry_t *entries, int32_t count);

/// Return the static built-in tracker-source list; writes the count to *out_count.
const aethernet_fmhy_tracker_source_t *aethernet_fmhy_tracker_sources(int32_t *out_count);

/// Opaque in-memory catalogue service handle.
typedef struct aethernet_fmhy_service aethernet_fmhy_service_t;

aethernet_fmhy_service_t *aethernet_fmhy_service_new(void);
void aethernet_fmhy_service_free(aethernet_fmhy_service_t *service);

/// Replace the catalogue from a fresh FMHY markdown string.
void aethernet_fmhy_sync(aethernet_fmhy_service_t *service, const char *markdown);

/// Number of entries currently loaded.
int32_t aethernet_fmhy_entry_count(aethernet_fmhy_service_t *service);

/**
 * Browse entries, optionally filtered by a case-insensitive category substring
 * (pass NULL/"" for all). Returns a newly-allocated COPY array (caller frees via
 * aethernet_fmhy_entries_free); writes the count to *out_count.
 */
aethernet_fmhy_entry_t *aethernet_fmhy_browse(aethernet_fmhy_service_t *service, const char *category_filter, int32_t *out_count);

/// As aethernet_fmhy_browse, but only starred entries.
aethernet_fmhy_entry_t *aethernet_fmhy_get_starred(aethernet_fmhy_service_t *service, const char *category_filter, int32_t *out_count);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_FMHY_H
