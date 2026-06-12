// SPDX-License-Identifier: MIT

package aethernet.identity

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to replace the
 * stable, phone-derived UHID on the public wire.
 *
 * ## The problem it solves
 * A node's UHID is `SHA-256(phone : deviceId : publicKey)` — stable for the life of the
 * install and carried in cleartext on every packet. A passive observer who never breaks any
 * encryption can therefore (a) follow any node indefinitely across time and place, and (b) —
 * because the value is phone-derived — attempt to confirm a suspected phone number by
 * recomputing the hash. That is a surveillance and targeting primitive, independent of the
 * fact that message contents are end-to-end encrypted.
 *
 * ## The design
 *     ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0 until length]
 * - `routingKey` is SECRET — derived from the node's identity secret via [deriveRoutingKey].
 *   It is NEVER derived from the public key.
 * - `epoch = floor(unixSeconds / epochSeconds)` — a 15-minute window by default.
 * - Two ERIDs from the same node in different epochs are cryptographically uncorrelated to an
 *   outside observer — no cross-time linkage, no phone recovery.
 *
 * The epoch is encoded big-endian (8-byte signed Long) so every language port produces
 * byte-identical input to the HMAC.
 */
object EphemeralRoutingId {

    /** Same Crockford base-32 alphabet as [AetherNetTag] (no I/L/O/U — visually unambiguous). */
    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    /** HKDF domain-separation label. Must match the C# reference (and every other port). */
    private val ROUTING_KEY_INFO = "aether-erid-routing-key-v1".toByteArray(Charsets.UTF_8)

    /** Default rotation window: 15 minutes, expressed in seconds. */
    const val DEFAULT_EPOCH_SECONDS: Long = 900

    /** Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy). */
    const val DEFAULT_LENGTH: Int = 16

    /**
     * Derives the 32-byte SECRET routing key from a node's identity secret (e.g. its Ed25519
     * private-key bytes). Domain-separated via HKDF-SHA256 (RFC 5869, no salt). MUST be fed a
     * secret — never a public value, or the rotation schedule becomes computable by anyone.
     *
     * @throws IllegalArgumentException if [identitySecret] is empty.
     */
    fun deriveRoutingKey(identitySecret: ByteArray): ByteArray {
        require(identitySecret.isNotEmpty()) { "identitySecret cannot be empty" }
        return hkdfSha256(identitySecret, ROUTING_KEY_INFO, 32)
    }

    /**
     * The epoch (rotation-window index) that contains the given Unix time. Negative
     * [unixSeconds] clamp to 0.
     *
     * @throws IllegalArgumentException if [epochSeconds] is not positive.
     */
    fun epochFor(unixSeconds: Long, epochSeconds: Long = DEFAULT_EPOCH_SECONDS): Long {
        require(epochSeconds > 0) { "epochSeconds must be positive" }
        val u = if (unixSeconds < 0) 0 else unixSeconds
        return u / epochSeconds
    }

    /** Derives the ERID for the epoch that contains [unixSeconds]. */
    fun derive(
        routingKey: ByteArray,
        unixSeconds: Long,
        epochSeconds: Long = DEFAULT_EPOCH_SECONDS,
        length: Int = DEFAULT_LENGTH,
    ): String = deriveForEpoch(routingKey, epochFor(unixSeconds, epochSeconds), length)

    /**
     * Derives the ERID for an explicit epoch number. The epoch is encoded big-endian so every
     * language port produces byte-identical input to the HMAC.
     *
     * @throws IllegalArgumentException if [routingKey] is empty or [length] is outside 1..51.
     */
    fun deriveForEpoch(routingKey: ByteArray, epoch: Long, length: Int = DEFAULT_LENGTH): String {
        require(routingKey.isNotEmpty()) { "routingKey cannot be empty" }
        require(length in 1..51) { "length must be 1..51 (SHA-256 is 256 bits = 51 base-32 chars)" }

        // 8-byte big-endian signed Long — matches BinaryPrimitives.WriteInt64BigEndian.
        val epochBytes = ByteArray(8)
        var e = epoch
        for (i in 7 downTo 0) {
            epochBytes[i] = (e and 0xFF).toByte()
            e = e ushr 8
        }

        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(routingKey, "HmacSHA256"))
        return base32(mac.doFinal(epochBytes), length)
    }

    /**
     * HKDF-SHA256 (RFC 5869). With no salt the spec mandates HashLen (32) zero bytes, matching
     * every other language port so the derived routing key is byte-identical.
     */
    private fun hkdfSha256(ikm: ByteArray, info: ByteArray, outputLen: Int): ByteArray {
        // Extract: PRK = HMAC(salt = zeros[32], IKM)
        val extract = Mac.getInstance("HmacSHA256")
        extract.init(SecretKeySpec(ByteArray(32), "HmacSHA256"))
        val prk = extract.doFinal(ikm)

        // Expand: T(n) = HMAC(PRK, T(n-1) || info || n) until outputLen bytes are filled.
        val out = ByteArray(outputLen)
        var filled = 0
        var counter = 1
        var prev = ByteArray(0)
        while (filled < outputLen) {
            val expand = Mac.getInstance("HmacSHA256")
            expand.init(SecretKeySpec(prk, "HmacSHA256"))
            expand.update(prev)
            expand.update(info)
            expand.update(counter.toByte())
            val t = expand.doFinal()
            val take = minOf(t.size, outputLen - filled)
            System.arraycopy(t, 0, out, filled, take)
            filled += take
            prev = t
            counter++
        }
        return out
    }

    /** Encodes the first `length * 5` bits of [data] as Crockford base-32, MSB first. */
    private fun base32(data: ByteArray, length: Int): String {
        val out = CharArray(length)
        var bitPos = 0
        for (i in 0 until length) {
            val byteIndex = bitPos ushr 3
            val bitOffset = bitPos and 7
            val hi = data[byteIndex].toInt() and 0xFF
            val lo = if (byteIndex + 1 < data.size) data[byteIndex + 1].toInt() and 0xFF else 0
            val window = (hi shl 8) or lo
            val v = (window ushr (11 - bitOffset)) and 0x1F
            out[i] = ALPHABET[v]
            bitPos += 5
        }
        return String(out)
    }
}
