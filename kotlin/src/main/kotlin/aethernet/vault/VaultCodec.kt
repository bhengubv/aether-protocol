// SPDX-License-Identifier: MIT
//
// File-level helpers over ReedSolomonCodec: split a plaintext blob into K systematic data shards
// (zero-padded), produce the full N-shard set, and reconstruct the original blob from any K surviving
// shards. Byte-identical to the C# / Go vault data layout: shardSize = ceil(size/K), data shard i is
// plaintext[i*shardSize .. (i+1)*shardSize] zero-padded, and recovery concatenates the K recovered
// data shards in index order then trims to the original size.

package aethernet.vault

/**
 * Slices [data] into K equal zero-padded data shards of length `shardSize = ceil(size/K)`. This is
 * the systematic prefix the encoder leaves unchanged.
 */
fun splitIntoDataShards(data: ByteArray, k: Int): Array<ByteArray> {
    require(k >= 1) { "K must be >= 1." }
    require(data.isNotEmpty()) { "data must not be empty." }
    val shardSize = (data.size + k - 1) / k
    return Array(k) { i ->
        val shard = ByteArray(shardSize)
        val offset = i * shardSize
        if (offset < data.size) {
            val length = minOf(shardSize, data.size - offset)
            data.copyInto(shard, 0, offset, offset + length)
        }
        shard
    }
}

/** Splits [data] into K systematic data shards and returns the full set of N = K+M shards. */
fun ReedSolomonCodec.encodeData(data: ByteArray): Array<ByteArray> =
    encode(splitIntoDataShards(data, dataShardCount))

/**
 * Reconstructs the original blob of [originalSize] bytes from any K surviving shards. [available]
 * maps a shard index (0…N-1) to its bytes. Throws if fewer than K shards are supplied.
 */
fun ReedSolomonCodec.reconstructData(available: Map<Int, ByteArray>, originalSize: Int): ByteArray {
    val dataShards = decodeDataShards(available)
    require(originalSize >= 0) { "originalSize must be >= 0." }

    val shardSize = dataShards[0].size
    val out = ByteArray(dataShardCount * shardSize)
    for (j in 0 until dataShardCount) {
        dataShards[j].copyInto(out, j * shardSize)
    }
    require(originalSize <= out.size) { "originalSize exceeds reconstructed length." }
    return out.copyOf(originalSize)
}
