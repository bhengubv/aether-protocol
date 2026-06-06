// SPDX-License-Identifier: MIT
//
// ChunkBitmapPayload wire-format codec — C implementation.
//
// Compact LSB-first bitset encoding for the ChunkBitmap broadcast packet
// (PacketType 37).  Bit i is set in byte (i>>3) at bit-position (i&7).
// Output length is exactly ceil(chunk_count / 8) bytes; trailing bits are
// always zero.
//
// Cross-language stable: the same bit-packing and Base64-with-padding
// encoding is implemented in C#, Go, Python, TypeScript, Rust, Kotlin,
// and Swift.  The canonical JSON field order is:
//   root_hash, chunk_count, have_bitset, generation

#ifndef AETHERMESH_CHUNK_BITMAP_H
#define AETHERMESH_CHUNK_BITMAP_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stddef.h>
#include <stdint.h>

// ── Bitset type ───────────────────────────────────────────────────────────────

/// A compact LSB-first bitset.
/// Free with aethermesh_bitset_free() when done.
typedef struct {
    uint8_t *bytes;  ///< Allocated bitset bytes; NULL when chunk_count == 0
    size_t   len;    ///< Byte count = ceil(chunk_count / 8)
} aethermesh_bitset_t;

// ── Codec API ─────────────────────────────────────────────────────────────────

/// Encode a list of present-chunk indices into an LSB-first compact bitset.
///
/// @param chunk_count  Total number of chunks in the content (>= 0).
/// @param have_indices Array of chunk indices that are present.
/// @param index_count  Length of have_indices[].
/// @return             Allocated bitset.  The caller MUST call
///                     aethermesh_bitset_free() on the result.
///                     On allocation failure, returns {NULL, 0}.
aethermesh_bitset_t aethermesh_bitset_encode(int           chunk_count,
                                     const int    *have_indices,
                                     int           index_count);

/// Decode a compact bitset into a sorted array of set-bit indices.
///
/// @param bitset      Bitset bytes (may be NULL when bitset_len == 0).
/// @param bitset_len  Number of bytes in bitset[].
/// @param chunk_count Total chunk count (bits beyond this limit are ignored).
/// @param out_count   Set to the number of indices in the returned array.
/// @return            Allocated int[] sorted ascending.  The caller MUST free()
///                    the returned pointer.  Returns NULL and *out_count = 0
///                    when no bits are set or on allocation failure.
int *aethermesh_bitset_decode(const uint8_t *bitset,
                          size_t         bitset_len,
                          int            chunk_count,
                          int           *out_count);

/// Free a bitset returned by aethermesh_bitset_encode().
void aethermesh_bitset_free(aethermesh_bitset_t bs);

// ── JSON marshal ─────────────────────────────────────────────────────────────

/// Produce the canonical wire JSON for a ChunkBitmapPayload:
///   {"root_hash":"...","chunk_count":N,"have_bitset":"<base64>","generation":G}
///
/// @param root_hash      Lowercase hex SHA-256 root hash (not escaped — must be
///                       a valid hex string with no JSON-special characters).
/// @param chunk_count    Total chunk count.
/// @param have_bitset    Bitset bytes.
/// @param have_bitset_len Number of bytes in have_bitset[].
/// @param generation     Generation counter (uint32_t).
/// @return               Freshly allocated NUL-terminated JSON string.  The
///                       caller MUST free() the returned pointer.  Returns NULL
///                       on allocation failure.
char *aethermesh_chunk_bitmap_marshal_json(const char    *root_hash,
                                       int            chunk_count,
                                       const uint8_t *have_bitset,
                                       size_t         have_bitset_len,
                                       uint32_t       generation);

#ifdef __cplusplus
}
#endif

#endif /* AETHERMESH_CHUNK_BITMAP_H */
