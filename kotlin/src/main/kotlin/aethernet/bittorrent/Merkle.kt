// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.security.MessageDigest

/** The BitTorrent v2 leaf-block size (BEP-52). */
const val MERKLE_BLOCK_SIZE = 16384

private fun sha256(data: ByteArray): ByteArray =
    MessageDigest.getInstance("SHA-256").digest(data)

/**
 * Computes the SHA-256 merkle root of [data] split into 16 KiB leaf blocks (BEP-52):
 * each block SHA-256'd, the leaf layer zero-padded (a zero hash is 32 zero bytes) to
 * the next power of two, internal nodes SHA-256(left||right). Byte-identical to the
 * reference `MerkleTree.ComputeRoot`. The Kotlin port of `go/bittorrent/merkle.go`.
 */
fun merkleRoot(data: ByteArray): ByteArray = merkleRootBlock(data, MERKLE_BLOCK_SIZE)

/** [merkleRoot] with an explicit block size. */
fun merkleRootBlock(data: ByteArray, blockSize: Int): ByteArray {
    require(blockSize > 0) { "block size must be positive" }
    val leaves = ArrayList<ByteArray>()
    var i = 0
    while (i < data.size) {
        val end = minOf(i + blockSize, data.size)
        leaves.add(sha256(data.copyOfRange(i, end)))
        i += blockSize
    }
    if (leaves.isEmpty()) return ByteArray(32) // empty content → zero root
    return merkleRootOf(leaves)
}

/** Combines leaf hashes into a root, zero-padding to the next power of two. */
private fun merkleRootOf(leafHashes: List<ByteArray>): ByteArray {
    if (leafHashes.isEmpty()) return ByteArray(32)
    var level = ArrayList<ByteArray>(leafHashes)

    var width = 1
    while (width < level.size) width = width shl 1
    val zero = ByteArray(32)
    while (level.size < width) level.add(zero)

    while (level.size > 1) {
        val next = ArrayList<ByteArray>(level.size / 2)
        var j = 0
        while (j < level.size) {
            val combined = ByteArray(64)
            System.arraycopy(level[j], 0, combined, 0, 32)
            System.arraycopy(level[j + 1], 0, combined, 32, 32)
            next.add(sha256(combined))
            j += 2
        }
        level = next
    }
    return level[0]
}

/** The full 32-byte BitTorrent v2 info-hash: SHA-256 of the bencoded info dict. */
fun bitTorrentV2InfoHash(infoDictBytes: ByteArray): ByteArray = sha256(infoDictBytes)

/** The BitTorrent v2 info-hash truncated to 20 bytes. */
fun bitTorrentV2InfoHashTruncated(infoDictBytes: ByteArray): ByteArray =
    bitTorrentV2InfoHash(infoDictBytes).copyOfRange(0, 20)
