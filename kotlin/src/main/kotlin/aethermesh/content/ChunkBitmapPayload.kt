// SPDX-License-Identifier: MIT
package aethermesh.content

import java.util.Base64

/**
 * ChunkBitmap wire-format codec for the Aether Chunk Shuffle / SAPI protocol.
 *
 * Wire format:
 *  - JSON, snake_case property names.
 *  - Bitset: LSB-first within each byte — bit i is set in byte (i/8) at
 *    position (i%8). Length = ceil(chunk_count / 8).
 *  - Bitset transmitted as standard Base64 (with padding).
 *  - Field order in canonical JSON: root_hash, chunk_count, have_bitset, generation.
 */
object BitsetCodec {

    /**
     * Encode chunk indices into an LSB-first compact bitset.
     * Returns a ByteArray of length ceil(chunkCount / 8).
     */
    fun encode(chunkCount: Int, haveIndices: Iterable<Int>): ByteArray {
        if (chunkCount <= 0) return ByteArray(0)
        val bytes = ByteArray((chunkCount + 7) / 8)
        for (i in haveIndices) {
            require(i in 0 until chunkCount) { "Index $i out of range [0, $chunkCount)" }
            bytes[i shr 3] = (bytes[i shr 3].toInt() or (1 shl (i and 7))).toByte()
        }
        return bytes
    }

    /**
     * Decode a compact bitset back to a sorted list of chunk indices.
     */
    fun decode(bitset: ByteArray, chunkCount: Int): List<Int> {
        val result = mutableListOf<Int>()
        val limit = minOf(chunkCount, bitset.size * 8)
        for (i in 0 until limit) {
            if ((bitset[i shr 3].toInt() and 0xFF) and (1 shl (i and 7)) != 0) {
                result.add(i)
            }
        }
        return result
    }
}

/**
 * Produce the canonical wire JSON for a ChunkBitmapPayload.
 * Field order: root_hash → chunk_count → have_bitset → generation.
 */
fun marshalChunkBitmapJson(
    rootHash: String,
    chunkCount: Int,
    haveBitset: ByteArray,
    generation: UInt,
): String {
    val b64 = Base64.getEncoder().encodeToString(haveBitset)
    return buildString {
        append("{")
        append("\"root_hash\":${jsonString(rootHash)}")
        append(",\"chunk_count\":$chunkCount")
        append(",\"have_bitset\":${jsonString(b64)}")
        append(",\"generation\":$generation")
        append("}")
    }
}

private fun jsonString(s: String): String = buildString {
    append('"')
    for (c in s) when (c) {
        '"'  -> append("\\\"")
        '\\' -> append("\\\\")
        else -> append(c)
    }
    append('"')
}
