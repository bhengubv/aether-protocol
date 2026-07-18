// SPDX-License-Identifier: MIT

package aethernet.bittorrent

/** A µTP packet type (BEP-29). */
object UtpPacketType {
    const val DATA = 0
    const val FIN = 1
    const val STATE = 2
    const val RESET = 3
    const val SYN = 4
}

/** The µTP protocol version this SDK speaks. */
const val UTP_VERSION = 1

/** The fixed µTP header length. */
const val UTP_HEADER_SIZE = 20

/**
 * A µTP packet (BEP-29, version 1). The 20-byte header is
 * type|version(1) · extension(1) · connection_id(2) · timestamp_us(4) ·
 * timestamp_diff_us(4) · wnd_size(4) · seq_nr(2) · ack_nr(2), all big-endian.
 * The 32-bit fields are held in Longs so the full unsigned range round-trips.
 * The Kotlin port of `go/bittorrent/utp.go`.
 */
class UtpPacket(
    val type: Int,
    val connectionId: Int,
    val timestampMicros: Long,
    val timestampDiff: Long,
    val windowSize: Long,
    val seqNr: Int,
    val ackNr: Int,
    val payload: ByteArray,
) {
    /** Serializes the packet (no extensions). */
    fun toBytes(): ByteArray {
        val buf = ByteArray(UTP_HEADER_SIZE + payload.size)
        buf[0] = ((type shl 4) or UTP_VERSION).toByte()
        buf[1] = 0 // no extensions
        putU16BE(buf, 2, connectionId)
        putU32BE(buf, 4, timestampMicros)
        putU32BE(buf, 8, timestampDiff)
        putU32BE(buf, 12, windowSize)
        putU16BE(buf, 16, seqNr)
        putU16BE(buf, 18, ackNr)
        System.arraycopy(payload, 0, buf, UTP_HEADER_SIZE, payload.size)
        return buf
    }

    companion object {
        /** Parses a µTP packet, walking any extension chain to find the payload. */
        fun parse(data: ByteArray): UtpPacket {
            require(data.size >= UTP_HEADER_SIZE) {
                "µTP packet is ${data.size} bytes, shorter than the $UTP_HEADER_SIZE-byte header"
            }
            val version = data[0].toInt() and 0x0F
            require(version == UTP_VERSION) { "unsupported µTP version $version" }
            val type = (data[0].toInt() and 0xff) ushr 4

            // Walk the extension chain (each: next_ext(1) len(1) data(len)).
            var offset = UTP_HEADER_SIZE
            var nextExt = data[1].toInt() and 0xff
            while (nextExt != 0) {
                require(offset + 2 <= data.size) { "truncated µTP extension header" }
                val thisNext = data[offset].toInt() and 0xff
                val extLen = data[offset + 1].toInt() and 0xff
                offset += 2 + extLen
                require(offset <= data.size) { "truncated µTP extension data" }
                nextExt = thisNext
            }

            return UtpPacket(
                type = type,
                connectionId = u16BE(data, 2),
                timestampMicros = u32BE(data, 4),
                timestampDiff = u32BE(data, 8),
                windowSize = u32BE(data, 12),
                seqNr = u16BE(data, 16),
                ackNr = u16BE(data, 18),
                payload = data.copyOfRange(offset, data.size),
            )
        }
    }
}
