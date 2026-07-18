// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.security.MessageDigest

/**
 * Holds verified pieces of a torrent in memory, verifying each against its SHA-1
 * before accepting it, and can serve blocks or assemble the whole content. The
 * Kotlin port of `go/bittorrent/piecestore.go`.
 */
class PieceStore(
    private val pieceLength: Int,
    private val totalLength: Long,
    private var pieceHashes: List<ByteArray>,
) {
    private val pieces = HashMap<Int, ByteArray>()

    /** The number of pieces. */
    fun pieceCount(): Int = pieceHashes.size

    /** The byte length of a piece (the last may be short). */
    fun lengthOfPiece(i: Int): Int {
        if (i < 0 || i >= pieceHashes.size) return 0
        if (i == pieceHashes.size - 1) return (totalLength - i.toLong() * pieceLength.toLong()).toInt()
        return pieceLength
    }

    /** Whether a verified piece is present. */
    fun has(i: Int): Boolean = pieces.containsKey(i)

    /** Verifies [data] against the piece's SHA-1 and stores it on success. */
    fun tryComplete(i: Int, data: ByteArray): Boolean {
        if (i < 0 || i >= pieceHashes.size) return false
        if (data.size != lengthOfPiece(i)) return false
        val h = sha1(data)
        if (!h.contentEquals(pieceHashes[i])) return false
        pieces[i] = data.copyOf()
        return true
    }

    /** Returns a block from a stored piece, or null. */
    fun readBlock(i: Int, begin: Int, length: Int): ByteArray? {
        val p = pieces[i] ?: return null
        if (begin < 0 || length < 0 || begin + length > p.size) return null
        return p.copyOfRange(begin, begin + length)
    }

    /** A bitfield of currently-held pieces. */
    fun buildBitfield(): Bitfield {
        val bf = Bitfield.of(pieceHashes.size)
        for (i in pieceHashes.indices) if (has(i)) bf.set(i)
        return bf
    }

    /** Whether every piece is present. */
    fun isComplete(): Boolean = pieces.size == pieceHashes.size

    /** Returns the full content if complete, else null. */
    fun assemble(): ByteArray? {
        if (!isComplete()) return null
        val out = ByteArray(totalLength.toInt())
        var off = 0
        for (i in pieceHashes.indices) {
            val p = pieces[i]!!
            System.arraycopy(p, 0, out, off, p.size)
            off += p.size
        }
        return out
    }

    companion object {
        /** Builds a complete store from raw content (a seeder's side). */
        fun fromContent(data: ByteArray, pieceLength: Int): PieceStore {
            val pieceCount = (data.size + pieceLength - 1) / pieceLength
            val hashes = ArrayList<ByteArray>(pieceCount)
            val s = PieceStore(pieceLength, data.size.toLong(), emptyList())
            for (i in 0 until pieceCount) {
                val start = i * pieceLength
                val end = minOf(start + pieceLength, data.size)
                hashes.add(sha1(data.copyOfRange(start, end)))
                s.pieces[i] = data.copyOfRange(start, end)
            }
            s.pieceHashes = hashes
            return s
        }
    }
}

private fun sha1(data: ByteArray): ByteArray =
    MessageDigest.getInstance("SHA-1").digest(data)
