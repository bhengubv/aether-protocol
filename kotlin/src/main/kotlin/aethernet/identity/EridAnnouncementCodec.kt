// SPDX-License-Identifier: MIT

package aethernet.identity

/**
 * Frames the in-session ERID announcement — the message a node sends a peer INSIDE an
 * established Signal session to share its secret `routingKey` (plus the rotation parameters it
 * uses), so the peer can resolve its rotating wire address via [EridDirectory].
 *
 * The bytes are carried *encrypted* by the Signal session, so this is framing only — no
 * encryption of its own. A 4-byte magic sentinel + version lets a receiver tell an ERID
 * announcement apart from other in-session application data before trying to parse it.
 *
 * Layout: magic `AERD` (4) + version (1) + epochSeconds (Int32 BE) + eridLength (Int32 BE) +
 * routingKeyLen (Int32 BE) + routingKey. Integer fields big-endian so every language port
 * frames byte-identically. Port of the C# reference.
 */
object EridAnnouncementCodec {

    /** A decoded in-session ERID announcement. */
    class Announcement(
        val routingKey: ByteArray,
        val epochSeconds: Int,
        val eridLength: Int,
    ) {
        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is Announcement) return false
            return routingKey.contentEquals(other.routingKey) &&
                epochSeconds == other.epochSeconds &&
                eridLength == other.eridLength
        }

        override fun hashCode(): Int =
            (routingKey.contentHashCode() * 31 + epochSeconds) * 31 + eridLength
    }

    // 'A' 'E' 'R' 'D' — "AetherNet ERID Directory announcement".
    private val MAGIC = byteArrayOf(0x41, 0x45, 0x52, 0x44)
    private const val VERSION: Byte = 1
    // magic(4) + version(1) + epochSeconds(4) + eridLength(4) + routingKeyLen(4).
    private const val HEADER_LENGTH = 17

    /**
     * Frame an announcement carrying [routingKey] and the rotation params.
     *
     * @throws IllegalArgumentException if [routingKey] is empty, [epochSeconds] is not
     *   positive, or [eridLength] is outside 1..51.
     */
    fun encode(
        routingKey: ByteArray,
        epochSeconds: Int = EphemeralRoutingId.DEFAULT_EPOCH_SECONDS.toInt(),
        eridLength: Int = EphemeralRoutingId.DEFAULT_LENGTH,
    ): ByteArray {
        require(routingKey.isNotEmpty()) { "routingKey cannot be empty" }
        require(epochSeconds > 0) { "epochSeconds must be positive" }
        require(eridLength in 1..51) { "eridLength must be 1..51" }

        val buf = ByteArray(HEADER_LENGTH + routingKey.size)
        System.arraycopy(MAGIC, 0, buf, 0, 4)
        buf[4] = VERSION
        writeInt32BE(buf, 5, epochSeconds)
        writeInt32BE(buf, 9, eridLength)
        writeInt32BE(buf, 13, routingKey.size)
        System.arraycopy(routingKey, 0, buf, HEADER_LENGTH, routingKey.size)
        return buf
    }

    /**
     * Parse an announcement. Returns null (rather than throwing) when the bytes are not a
     * well-formed ERID announcement, so a receiver can cheaply test an arbitrary decrypted
     * in-session payload against the magic.
     */
    fun tryDecode(data: ByteArray): Announcement? {
        if (data.size < HEADER_LENGTH) return null
        for (i in 0 until 4) if (data[i] != MAGIC[i]) return null
        if (data[4] != VERSION) return null

        val epochSeconds = readInt32BE(data, 5)
        val eridLength = readInt32BE(data, 9)
        val keyLen = readInt32BE(data, 13)

        if (epochSeconds <= 0) return null
        if (eridLength < 1 || eridLength > 51) return null
        if (keyLen <= 0 || HEADER_LENGTH.toLong() + keyLen.toLong() > data.size.toLong()) return null

        return Announcement(data.copyOfRange(HEADER_LENGTH, HEADER_LENGTH + keyLen), epochSeconds, eridLength)
    }

    private fun writeInt32BE(buf: ByteArray, offset: Int, value: Int) {
        buf[offset] = ((value ushr 24) and 0xFF).toByte()
        buf[offset + 1] = ((value ushr 16) and 0xFF).toByte()
        buf[offset + 2] = ((value ushr 8) and 0xFF).toByte()
        buf[offset + 3] = (value and 0xFF).toByte()
    }

    private fun readInt32BE(buf: ByteArray, offset: Int): Int =
        ((buf[offset].toInt() and 0xFF) shl 24) or
            ((buf[offset + 1].toInt() and 0xFF) shl 16) or
            ((buf[offset + 2].toInt() and 0xFF) shl 8) or
            (buf[offset + 3].toInt() and 0xFF)
}
