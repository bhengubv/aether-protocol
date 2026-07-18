// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.io.ByteArrayOutputStream

/**
 * Big-endian byte helpers shared across the BitTorrent codec. The BitTorrent
 * wire format is big-endian throughout (peer-wire length framing, µTP header,
 * compact node/peer records), so these mirror the `binary.BigEndian` helpers in
 * the Go reference (`go/bittorrent`).
 */

/** Unsigned lexicographic comparison — the ordering BEP-3 mandates for dictionary keys. */
internal fun compareBytesUnsigned(a: ByteArray, b: ByteArray): Int {
    val n = minOf(a.size, b.size)
    for (i in 0 until n) {
        val ai = a[i].toInt() and 0xff
        val bi = b[i].toInt() and 0xff
        if (ai != bi) return ai - bi
    }
    return a.size - b.size
}

/** Writes a big-endian unsigned 16-bit value into [buf] at [off]. */
internal fun putU16BE(buf: ByteArray, off: Int, v: Int) {
    buf[off] = ((v ushr 8) and 0xff).toByte()
    buf[off + 1] = (v and 0xff).toByte()
}

/** Writes a big-endian unsigned 32-bit value (held in a Long) into [buf] at [off]. */
internal fun putU32BE(buf: ByteArray, off: Int, v: Long) {
    buf[off] = ((v ushr 24) and 0xff).toByte()
    buf[off + 1] = ((v ushr 16) and 0xff).toByte()
    buf[off + 2] = ((v ushr 8) and 0xff).toByte()
    buf[off + 3] = (v and 0xff).toByte()
}

/** Reads a big-endian unsigned 16-bit value from [data] at [off]. */
internal fun u16BE(data: ByteArray, off: Int): Int =
    ((data[off].toInt() and 0xff) shl 8) or (data[off + 1].toInt() and 0xff)

/** Reads a big-endian unsigned 32-bit value from [data] at [off] into a Long. */
internal fun u32BE(data: ByteArray, off: Int): Long =
    ((data[off].toLong() and 0xff) shl 24) or
        ((data[off + 1].toLong() and 0xff) shl 16) or
        ((data[off + 2].toLong() and 0xff) shl 8) or
        (data[off + 3].toLong() and 0xff)

/** Appends a big-endian unsigned 16-bit value to [out]. */
internal fun writeU16BE(out: ByteArrayOutputStream, v: Int) {
    out.write((v ushr 8) and 0xff)
    out.write(v and 0xff)
}

/** Appends a big-endian unsigned 32-bit value (held in a Long) to [out]. */
internal fun writeU32BE(out: ByteArrayOutputStream, v: Long) {
    out.write(((v ushr 24) and 0xff).toInt())
    out.write(((v ushr 16) and 0xff).toInt())
    out.write(((v ushr 8) and 0xff).toInt())
    out.write((v and 0xff).toInt())
}
