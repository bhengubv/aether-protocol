// SPDX-License-Identifier: MIT
//
// ChunkBitmapPayload wire-format codec — Swift implementation.
//
// Compact LSB-first bitset encoding for the ChunkBitmap broadcast packet
// (PacketType 37). Bit i is set in byte (i>>3) at bit-position (i&7).
// Output length is exactly ceil(chunk_count / 8) bytes; trailing bits are
// always zero.
//
// Cross-language stable: the same bit-packing and Base64-with-padding
// encoding is implemented in C, C#, Go, Python, TypeScript, Rust, Kotlin.
// The canonical JSON field order is:
//   root_hash, chunk_count, have_bitset, generation

import Foundation

// ── BitsetCodec ────────────────────────────────────────────────────────────────

/// Compact LSB-first bitset codec for ChunkBitmapPayload.
public enum BitsetCodec {

    /// Encode a list of present-chunk indices into an LSB-first compact bitset.
    ///
    /// - Parameters:
    ///   - chunkCount: Total number of chunks in the content (≥ 0).
    ///   - haveIndices: Chunk indices that are present. Out-of-range values
    ///     are silently ignored.
    /// - Returns: `Data` of length `ceil(chunkCount / 8)`, or empty when
    ///   `chunkCount == 0`.
    public static func encode(chunkCount: Int, haveIndices: [Int]) -> Data {
        guard chunkCount > 0 else { return Data() }
        let byteLen = (chunkCount + 7) / 8
        var bytes = Data(repeating: 0, count: byteLen)
        for idx in haveIndices {
            guard idx >= 0 && idx < chunkCount else { continue }
            bytes[idx >> 3] |= UInt8(1 << (idx & 7))
        }
        return bytes
    }

    /// Decode a compact bitset into a sorted array of set-bit indices.
    ///
    /// - Parameters:
    ///   - bitset: Compact bitset bytes.
    ///   - chunkCount: Total chunk count; bits beyond this limit are ignored.
    /// - Returns: Sorted `[Int]` of set-bit indices. Empty when no bits are
    ///   set or `bitset` is empty.
    public static func decode(bitset: Data, chunkCount: Int) -> [Int] {
        guard !bitset.isEmpty && chunkCount > 0 else { return [] }
        let limit = min(chunkCount, bitset.count * 8)
        var result: [Int] = []
        for i in 0..<limit where (bitset[i >> 3] & UInt8(1 << (i & 7))) != 0 {
            result.append(i)
        }
        return result
    }
}

// ── JSON marshal ───────────────────────────────────────────────────────────────

/// Produce the canonical wire JSON for a ChunkBitmapPayload:
///
///     {"root_hash":"...","chunk_count":N,"have_bitset":"<base64>","generation":G}
///
/// Field order is fixed by the cross-language specification.
/// Base64 encoding uses RFC 4648 §4 (standard alphabet, padded) via
/// `Data.base64EncodedString()`.
///
/// - Parameters:
///   - rootHash: Lowercase hex SHA-256 root hash (no JSON-special characters).
///   - chunkCount: Total chunk count.
///   - haveBitset: Bitset bytes returned by `BitsetCodec.encode`.
///   - generation: Generation counter (`UInt32`).
/// - Returns: Canonical JSON string.
public func marshalChunkBitmapJson(
    rootHash: String,
    chunkCount: Int,
    haveBitset: Data,
    generation: UInt32
) -> String {
    let b64 = haveBitset.base64EncodedString()
    // Single-line concatenation guarantees no embedded newlines in the output.
    return "{\"root_hash\":\"\(rootHash)\",\"chunk_count\":\(chunkCount),"
         + "\"have_bitset\":\"\(b64)\",\"generation\":\(generation)}"
}
