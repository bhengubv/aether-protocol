// SPDX-License-Identifier: MIT

package aether.reputation

import aether.protocol.PacketType
import aether.security.NodeReputationService
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * Signed peer-to-peer reputation-score propagation service.
 *
 * Implements the gossip half of the Aether security hardening layer:
 * - **Broadcast**: serialize, sign, and fan-out a [ReputationUpdatePayload] to
 *   all directly-connected peers.
 * - **Receive**: verify signature, apply freshness / echo guards, weight the
 *   incoming delta by the reporter's own reputation, and commit the effective
 *   delta to [NodeReputationService].
 *
 * Wire format: `PacketType.ReputationUpdate` (value 52), payload is
 * UTF-8 JSON (snake_case keys via [SerialName] annotations).
 *
 * Freshness window: ±5 minutes. Stale or future-dated packets are silently
 * dropped. Self-echo guard: the node never applies its own re-broadcast.
 *
 * Effective delta formula:
 * ```
 * effectiveDelta = clamp(scoreDelta, −1, 1) × reporterReputation
 * ```
 * Unknown reporters default to reputation 1.0 (benefit of the doubt).
 */
class ReputationGossipService(
    private val meshSender: MeshSender,
    private val packetSigner: PacketSigner,
    private val reputation: NodeReputationService,
) {

    // ── Injectable abstractions ───────────────────────────────────────────────

    interface MeshSender {
        val localUhid: String
        fun broadcast(packet: GossipPacket): Int
    }

    interface PacketSigner {
        fun sign(packet: GossipPacket)
        fun verify(packet: GossipPacket, senderPublicKey: ByteArray): Boolean
    }

    // ── Wire types ────────────────────────────────────────────────────────────

    @Serializable
    data class ReputationUpdatePayload(
        @SerialName("reporter_uhid") val reporterUhid: String,
        @SerialName("target_uhid")   val targetUhid: String,
        @SerialName("score_delta")   val scoreDelta: Double,
        @SerialName("timestamp_ms")  val timestampMs: Long,
        val reason: String,
    )

    data class GossipPacket(
        val packetType: Byte,
        val sourceUhid: String,
        val destinationUhid: String,
        val ttl: Int,
        val payload: ByteArray,
        val timestampMs: Long,
        var signature: ByteArray = ByteArray(0),
        var packetNonce: ByteArray = ByteArray(0),
    )

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Build, sign, and broadcast a [ReputationUpdatePayload] to all
     * directly-connected peers.
     *
     * [scoreDelta] is clamped to [−1, 1] before serialisation.
     * Returns the broadcast fan-out count.
     */
    fun broadcastReputationUpdate(targetUhid: String, scoreDelta: Double, reason: String): Int {
        val clamped = scoreDelta.coerceIn(-1.0, 1.0)
        val nowMs = System.currentTimeMillis()
        val payload = ReputationUpdatePayload(
            reporterUhid = meshSender.localUhid,
            targetUhid   = targetUhid,
            scoreDelta   = clamped,
            timestampMs  = nowMs,
            reason       = reason,
        )
        val payloadBytes = Json.encodeToString(payload).toByteArray(Charsets.UTF_8)
        val packet = GossipPacket(
            packetType       = PacketType.ReputationUpdate.value,
            sourceUhid       = meshSender.localUhid,
            destinationUhid  = "*",
            ttl              = 3,
            payload          = payloadBytes,
            timestampMs      = nowMs,
        )
        packetSigner.sign(packet)
        return meshSender.broadcast(packet)
    }

    /**
     * Process an inbound gossip packet.
     *
     * Returns `true` if the packet was accepted and the weighted delta was
     * applied to the local reputation service. Returns `false` (no-op) if any
     * guard check fails.
     *
     * Guards (in order):
     * 1. Packet type must be [PacketType.ReputationUpdate].
     * 2. Signature must be valid.
     * 3. Payload must be valid JSON.
     * 4. Freshness: |now − timestampMs| ≤ 5 minutes.
     * 5. Non-empty reporter and target UHIDs.
     * 6. Own-echo: reporter must not be this node.
     * 7. Apply `effectiveDelta = clamp(scoreDelta, −1, 1) × reporterReputation`.
     */
    fun handleGossipPacket(packet: GossipPacket, senderPublicKey: ByteArray): Boolean {
        // 1. Type guard
        if (packet.packetType != PacketType.ReputationUpdate.value) return false
        // 2. Signature
        if (!packetSigner.verify(packet, senderPublicKey)) return false
        // 3. Parse payload
        val payload = try {
            Json.decodeFromString<ReputationUpdatePayload>(
                packet.payload.toString(Charsets.UTF_8)
            )
        } catch (_: Exception) {
            return false
        }
        // 4. Freshness (±5 min)
        val nowMs = System.currentTimeMillis()
        if (kotlin.math.abs(nowMs - payload.timestampMs) > FRESHNESS_WINDOW_MS) return false
        // 5. Non-empty UHIDs
        if (payload.reporterUhid.isEmpty() || payload.targetUhid.isEmpty()) return false
        // 6. Own-echo guard
        if (payload.reporterUhid == meshSender.localUhid) return false
        // 7. Weighted delta
        val reporterScore = reputation.getReputationScore(payload.reporterUhid)
        val clamped = payload.scoreDelta.coerceIn(-1.0, 1.0)
        val effective = clamped * reporterScore
        reputation.applyWeightedDelta(payload.targetUhid, effective)
        return true
    }

    private companion object {
        const val FRESHNESS_WINDOW_MS = 5L * 60L * 1000L // 5 minutes
    }
}
