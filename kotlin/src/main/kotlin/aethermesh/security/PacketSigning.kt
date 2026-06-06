// SPDX-License-Identifier: MIT

package aethermesh.security

import aethermesh.protocol.MeshPacket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/**
 * Packet signing utilities with nonce deduplication.
 *
 * Constructs signable data matching the C# implementation exactly:
 * PacketNonce || TimestampMs || Type || SourceUhidLength || SourceUhid ||
 * DestinationUhidLength || DestinationUhid || SHA-256(Payload) || Ttl || Priority
 *
 * Nonce deduplication is keyed by `(sourceUhid, nonce)` so that:
 *  - a nonce collision across two different senders does NOT drop a
 *    legitimate packet, and
 *  - an attacker who pre-registers a nonce against a recipient cannot block
 *    the legitimate sender's first packet.
 *
 * Pre-2026-05-05 the cache used a `Pair<String, ByteArray>` key, but
 * `ByteArray.hashCode` / `equals` are identity-based — two distinct
 * `ByteArray` instances with identical bytes hashed differently and never
 * collided, so dedup silently never fired. Switched to a string key
 * `"<source>:<hex(nonce)>"` to match the C# reference (SourceUhid + ":" +
 * Convert.ToHexString(PacketNonce)).
 */
object PacketSigning {
    private val nonceDedupCache = ConcurrentHashMap<String, Long>()
    private const val MAX_PACKET_AGE_MS = 300_000L // 5 minutes
    private val HEX_CHARS = "0123456789ABCDEF".toCharArray()

    /**
     * Optional reputation hook.  When set, replay attempts and signature
     * failures are reported so the calling layer can down-score the offending
     * peer.  `null` (the default) disables all reputation side-effects without
     * changing validation semantics.
     */
    @Volatile
    var reputation: NodeReputationService? = null

    /** Convenience setter for injection from Java or builder-style callers. */
    fun setReputationService(service: NodeReputationService?) {
        reputation = service
    }

    /**
     * Constructs the signable data for a packet.
     *
     * Wire format (little-endian):
     *   PacketNonce (8 bytes)
     *   TimestampMs (8 bytes, int64)
     *   Type (4 bytes, int32)
     *   SourceUhidLength (4 bytes, int32)
     *   SourceUhid (UTF-8 bytes)
     *   DestinationUhidLength (4 bytes, int32)
     *   DestinationUhid (UTF-8 bytes)
     *   SHA-256(Payload) (32 bytes)
     *   Ttl (4 bytes, int32)
     *   Priority (4 bytes, int32)
     */
    fun constructSignableData(packet: MeshPacket): ByteArray {
        val sourceBytes = packet.sourceUhid.toByteArray(Charsets.UTF_8)
        val destBytes = packet.destinationUhid.toByteArray(Charsets.UTF_8)
        val payloadHash = computeSHA256(packet.payload)

        val buffer = ByteBuffer.allocate(
            8 +  // PacketNonce
                    8 +  // TimestampMs
                    4 +  // Type
                    4 + sourceBytes.size +  // SourceUhid
                    4 + destBytes.size +  // DestinationUhid
                    32 +  // SHA-256(Payload)
                    4 +  // Ttl
                    4    // Priority
        ).apply {
            order(ByteOrder.LITTLE_ENDIAN)
        }

        // PacketNonce
        buffer.put(packet.packetNonce)

        // TimestampMs
        buffer.putLong(packet.timestampMs)

        // Type
        buffer.putInt(packet.type.value.toInt())

        // SourceUhid length and data
        buffer.putInt(sourceBytes.size)
        buffer.put(sourceBytes)

        // DestinationUhid length and data
        buffer.putInt(destBytes.size)
        buffer.put(destBytes)

        // SHA-256(Payload)
        buffer.put(payloadHash)

        // Ttl
        buffer.putInt(packet.ttl)

        // Priority
        buffer.putInt(packet.priority.toInt())

        return buffer.array()
    }

    /**
     * Signs a packet using Ed25519.
     *
     * @param packet The packet to sign
     * @param privateKey 32-byte Ed25519 private key
     * @return 64-byte signature
     */
    fun signPacket(packet: MeshPacket, privateKey: ByteArray): ByteArray {
        val signableData = constructSignableData(packet)
        return Ed25519Service.sign(privateKey, signableData)
    }

    /**
     * Verifies a packet signature using Ed25519.
     *
     * @param packet The packet to verify
     * @param publicKey 32-byte Ed25519 public key
     * @return True if the signature is valid
     */
    fun verifyPacket(packet: MeshPacket, publicKey: ByteArray): Boolean {
        val signableData = constructSignableData(packet)
        val valid = Ed25519Service.verify(publicKey, signableData, packet.signature)
        if (!valid) reputation?.recordSignatureFailure(packet.sourceUhid)
        return valid
    }

    /**
     * Checks if a packet nonce has been seen before (replay prevention).
     * Returns true if the nonce is NEW (not a replay).
     *
     * Maintains a deduplication cache with a 5-minute TTL keyed by
     * `(sourceUhid, hex(nonce))` — see class-level docs for the rationale
     * vs. nonce-only keying.
     *
     * @param packet The packet to check
     * @return True if this is a new packet, false if it's a replay
     */
    fun isNewPacket(packet: MeshPacket): Boolean {
        val key = nonceKey(packet.sourceUhid, packet.packetNonce)
        val now = System.currentTimeMillis()

        // Clean old entries.
        nonceDedupCache.entries.removeAll { (_, timestamp) ->
            (now - timestamp) > MAX_PACKET_AGE_MS
        }

        // putIfAbsent is the atomic "first writer wins" check we want — if
        // it returns non-null, the nonce was already seen (replay).
        val isNew = nonceDedupCache.putIfAbsent(key, now) == null
        if (!isNew) reputation?.recordReplayAttempt(packet.sourceUhid)
        return isNew
    }

    /**
     * Composite dedup key: `"<sourceUhid>:<HEX(nonce)>"`. Hex is uppercase
     * to match `Convert.ToHexString` in the C# reference, so cross-language
     * peers logging the same key produce identical strings.
     */
    private fun nonceKey(sourceUhid: String, nonce: ByteArray): String {
        val sb = StringBuilder(sourceUhid.length + 1 + nonce.size * 2)
        sb.append(sourceUhid).append(':')
        for (b in nonce) {
            val v = b.toInt() and 0xFF
            sb.append(HEX_CHARS[v ushr 4])
            sb.append(HEX_CHARS[v and 0x0F])
        }
        return sb.toString()
    }

    /**
     * Test-only: clear the dedup cache. Production code never needs this —
     * entries TTL out automatically.
     */
    internal fun clearDedupCacheForTests() {
        nonceDedupCache.clear()
    }

    /**
     * Test-only: reset the reputation service reference to null so tests are
     * isolated from one another.
     */
    internal fun clearReputationServiceForTests() {
        reputation = null
    }

    /**
     * Computes SHA-256 hash of data.
     */
    private fun computeSHA256(data: ByteArray): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(data)
    }
}
