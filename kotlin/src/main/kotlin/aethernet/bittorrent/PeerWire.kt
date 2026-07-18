// SPDX-License-Identifier: MIT

package aethernet.bittorrent

/**
 * BitTorrent peer-wire protocol (BEP-3): the 68-byte handshake, length-framed
 * messages (exact 4-byte big-endian length prefix), and the MSB-first bitfield.
 * The Kotlin port of `go/bittorrent/peerwire.go`.
 */

private const val PROTOCOL_STRING = "BitTorrent protocol"

/** Peer-wire message ids (BEP-3, plus 20 = BEP-10 extended). */
object PeerMessageId {
    const val CHOKE = 0
    const val UNCHOKE = 1
    const val INTERESTED = 2
    const val NOT_INTERESTED = 3
    const val HAVE = 4
    const val BITFIELD = 5
    const val REQUEST = 6
    const val PIECE = 7
    const val CANCEL = 8
    const val PORT = 9
    const val EXTENDED = 20
}

/**
 * The 68-byte BitTorrent peer-wire handshake:
 * pstrlen(1)=19 · "BitTorrent protocol"(19) · reserved(8) · info_hash(20) · peer_id(20).
 */
class Handshake(
    val reserved: ByteArray,
    val infoHash: ByteArray,
    val peerId: ByteArray,
) {
    /** Serializes the 68-byte handshake. */
    fun toBytes(): ByteArray {
        val buf = ByteArray(68)
        buf[0] = 19
        System.arraycopy(PROTOCOL_STRING.toByteArray(Charsets.US_ASCII), 0, buf, 1, 19)
        System.arraycopy(reserved, 0, buf, 20, 8)
        System.arraycopy(infoHash, 0, buf, 28, 20)
        System.arraycopy(peerId, 0, buf, 48, 20)
        return buf
    }

    /** Whether the reserved bits advertise the extension protocol (BEP-10). */
    fun supportsExtended(): Boolean = (reserved[5].toInt() and 0x10) != 0

    /** Whether the reserved bits advertise DHT (BEP-5). */
    fun supportsDht(): Boolean = (reserved[7].toInt() and 0x01) != 0

    companion object {
        /** Reserved bytes advertising the extension protocol (BEP-10) and DHT (BEP-5). */
        fun defaultReserved(): ByteArray {
            val r = ByteArray(8)
            r[5] = (r[5].toInt() or 0x10).toByte() // extension protocol
            r[7] = (r[7].toInt() or 0x01).toByte() // DHT
            return r
        }

        /** Parses a 68-byte handshake. */
        fun parse(data: ByteArray): Handshake {
            require(data.size >= 68) { "handshake is ${data.size} bytes, need 68" }
            require(data[0].toInt() == 19) { "handshake pstrlen is ${data[0].toInt()}, want 19" }
            require(String(data, 1, 19, Charsets.US_ASCII) == PROTOCOL_STRING) {
                "handshake protocol string mismatch"
            }
            return Handshake(
                reserved = data.copyOfRange(20, 28),
                infoHash = data.copyOfRange(28, 48),
                peerId = data.copyOfRange(48, 68),
            )
        }
    }
}

/**
 * A peer-wire message. A keep-alive has [hasId] = false (a zero-length frame).
 * [id] is a [PeerMessageId] value; [payload] excludes the id byte and length prefix.
 */
class PeerMessage(
    val hasId: Boolean,
    val id: Int,
    val payload: ByteArray,
) {
    /** Serializes the message with its 4-byte big-endian length prefix. */
    fun toBytes(): ByteArray {
        if (!hasId) return byteArrayOf(0, 0, 0, 0) // keep-alive
        val length = 1 + payload.size
        val buf = ByteArray(4 + length)
        putU32BE(buf, 0, length.toLong())
        buf[4] = id.toByte()
        System.arraycopy(payload, 0, buf, 5, payload.size)
        return buf
    }

    /** Decodes a Have payload. */
    fun havePieceIndex(): Long {
        require(id == PeerMessageId.HAVE && payload.size == 4) { "not a valid have message" }
        return u32BE(payload, 0)
    }

    /** Decodes a Request/Cancel payload as (index, begin, length). */
    fun blockRef(): Triple<Long, Long, Long> {
        require((id == PeerMessageId.REQUEST || id == PeerMessageId.CANCEL) && payload.size == 12) {
            "not a valid request/cancel message"
        }
        return Triple(u32BE(payload, 0), u32BE(payload, 4), u32BE(payload, 8))
    }

    /** Decodes a Port payload. */
    fun portValue(): Int {
        require(id == PeerMessageId.PORT && payload.size == 2) { "not a valid port message" }
        return u16BE(payload, 0)
    }

    companion object {
        private val EMPTY = ByteArray(0)

        /** The zero-length keep-alive message. */
        fun keepAlive(): PeerMessage = PeerMessage(false, 0, EMPTY)

        /** A message with an id and payload. */
        fun message(id: Int, payload: ByteArray): PeerMessage = PeerMessage(true, id, payload)

        fun choke(): PeerMessage = message(PeerMessageId.CHOKE, EMPTY)
        fun unchoke(): PeerMessage = message(PeerMessageId.UNCHOKE, EMPTY)
        fun interested(): PeerMessage = message(PeerMessageId.INTERESTED, EMPTY)
        fun notInterested(): PeerMessage = message(PeerMessageId.NOT_INTERESTED, EMPTY)

        fun have(pieceIndex: Long): PeerMessage {
            val p = ByteArray(4)
            putU32BE(p, 0, pieceIndex)
            return message(PeerMessageId.HAVE, p)
        }

        fun bitfieldMsg(bits: ByteArray): PeerMessage = message(PeerMessageId.BITFIELD, bits)

        fun request(index: Long, begin: Long, length: Long): PeerMessage {
            val p = ByteArray(12)
            putU32BE(p, 0, index)
            putU32BE(p, 4, begin)
            putU32BE(p, 8, length)
            return message(PeerMessageId.REQUEST, p)
        }

        fun cancel(index: Long, begin: Long, length: Long): PeerMessage {
            val p = ByteArray(12)
            putU32BE(p, 0, index)
            putU32BE(p, 4, begin)
            putU32BE(p, 8, length)
            return message(PeerMessageId.CANCEL, p)
        }

        fun piece(index: Long, begin: Long, block: ByteArray): PeerMessage {
            val p = ByteArray(8 + block.size)
            putU32BE(p, 0, index)
            putU32BE(p, 4, begin)
            System.arraycopy(block, 0, p, 8, block.size)
            return message(PeerMessageId.PIECE, p)
        }

        fun port(port: Int): PeerMessage {
            val p = ByteArray(2)
            putU16BE(p, 0, port)
            return message(PeerMessageId.PORT, p)
        }

        fun extended(subId: Int, body: ByteArray): PeerMessage {
            val p = ByteArray(1 + body.size)
            p[0] = subId.toByte()
            System.arraycopy(body, 0, p, 1, body.size)
            return message(PeerMessageId.EXTENDED, p)
        }

        /** Parses a message body (id + payload, no length prefix). Empty = keep-alive. */
        fun parseBody(body: ByteArray): PeerMessage {
            if (body.isEmpty()) return keepAlive()
            return message(body[0].toInt() and 0xff, body.copyOfRange(1, body.size))
        }

        /** Parses a full length-prefixed frame, returning the message and bytes consumed. */
        fun parseFrame(data: ByteArray): Pair<PeerMessage, Int> {
            require(data.size >= 4) { "frame shorter than 4-byte length prefix" }
            val length = u32BE(data, 0).toInt()
            require(length + 4 <= data.size) { "frame length $length exceeds available ${data.size - 4}" }
            return parseBody(data.copyOfRange(4, 4 + length)) to (4 + length)
        }
    }
}

/**
 * A piece bitfield, MSB-first: piece 0 is the 0x80 bit of byte 0 (BEP-3). The Kotlin
 * port of the Go `Bitfield`.
 */
class Bitfield private constructor(private val bits: ByteArray, val count: Int) {

    fun get(i: Int): Boolean {
        if (i < 0 || i >= count) return false
        return (bits[i ushr 3].toInt() and (0x80 ushr (i and 7))) != 0
    }

    fun set(i: Int) {
        if (i < 0 || i >= count) return
        bits[i ushr 3] = (bits[i ushr 3].toInt() or (0x80 ushr (i and 7))).toByte()
    }

    fun popCount(): Int {
        var n = 0
        for (i in 0 until count) if (get(i)) n++
        return n
    }

    fun hasAll(): Boolean = popCount() == count

    fun toBytes(): ByteArray = bits.copyOf()

    companion object {
        /** Allocates a cleared bitfield for [pieceCount] pieces. */
        fun of(pieceCount: Int): Bitfield = Bitfield(ByteArray((pieceCount + 7) / 8), pieceCount)

        /** Wraps received bytes for [pieceCount] pieces. */
        fun fromBytes(data: ByteArray, pieceCount: Int): Bitfield {
            val need = (pieceCount + 7) / 8
            val b = ByteArray(need)
            System.arraycopy(data, 0, b, 0, minOf(need, data.size))
            return Bitfield(b, pieceCount)
        }
    }
}
