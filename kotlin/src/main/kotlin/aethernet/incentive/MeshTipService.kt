// SPDX-License-Identifier: MIT

package aethernet.incentive

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import org.slf4j.LoggerFactory

/**
 * Default mesh-tip service. Sends and receives generic
 * [aethernet.protocol.PacketType.TipPacket] (24) packets. Kotlin port of
 * `AetherNet.Security.Services.MeshTipService`, mirroring the Go
 * `MeshTipService`.
 *
 * **Send path:** build a [TipPacketPayload] → sign the payload's canonical bytes
 * with the local identity key (real Ed25519) → serialise as snake_case JSON → wrap
 * in a [MeshPacket] → sign the enclosing packet → route toward the recipient
 * (unicast over a discovered route, falling back to broadcast).
 *
 * **Receive path:** deserialise the payload → best-effort signature check (Ed25519
 * signature must be present and well-formed = 64 bytes) → hand to the host's
 * [MeshTipSettlementProvider] → relay the packet onward toward its addressed
 * recipient. A malformed or unverifiable payload is logged and dropped, never
 * thrown.
 *
 * This service is purely a protocol mechanism. It attaches NO value semantics to
 * the amount and performs NO settlement — settlement is entirely the host's
 * business, expressed through the injected provider. A bare node (default no-op
 * provider) accepts and relays tips but settles nothing.
 */
class MeshTipService(
    private val sender: MeshSender,
    private val signer: PacketSigner,
    private val identity: IdentitySigner,
    private val routing: RouteResolver? = null,
    settle: MeshTipSettlementProvider? = null,
) {
    private val settle: MeshTipSettlementProvider = settle ?: NoopMeshTipSettlementProvider()

    // ── Injectable abstractions ───────────────────────────────────────────────

    /** Minimal mesh transport surface needed by [MeshTipService]. */
    interface MeshSender {
        /** The UHID of the local node. */
        val localUhid: String

        /** Delivers [packet] toward [nextHopUhid]. Returns true on success. */
        suspend fun send(packet: MeshPacket, nextHopUhid: String): Boolean

        /** Sends [packet] to every directly-connected peer; returns the fan-out count. */
        suspend fun broadcast(packet: MeshPacket): Int
    }

    /** Signs the enclosing [MeshPacket] envelope (fills signature / nonce / timestamp). */
    interface PacketSigner {
        /** Populates [packet]'s envelope signature, nonce, and timestamp fields in place. */
        fun sign(packet: MeshPacket)
    }

    /** Signs the tip payload's canonical bytes with the local node's Ed25519 identity key. */
    interface IdentitySigner {
        /** Produces a 64-byte Ed25519 signature over [data] using the local identity key. */
        fun signData(data: ByteArray): ByteArray
    }

    /**
     * Resolves a next-hop toward a destination UHID. Returns the next hop, or null
     * to fall back to broadcast.
     */
    interface RouteResolver {
        fun findNextHop(destinationUhid: String): String?
    }

    /**
     * The host's settlement hook — the Kotlin analog of the C#
     * `IAetherNetIncentiveProvider.SettleMeshTip`. It receives the full signed
     * [TipPacketPayload] off the mesh and decides how (if at all) to interpret its
     * value. The default no-op settles nothing.
     */
    interface MeshTipSettlementProvider {
        /**
         * Invoked for every inbound, well-formed tip payload. Implementations
         * (e.g. SDPKT / BhenguPay) wire their wallet settlement here. Throwing is
         * caught and logged by the caller but never propagated to the wire — a
         * settlement failure must not break relaying.
         */
        suspend fun settleMeshTip(payload: TipPacketPayload)
    }

    /**
     * The default no-op settlement provider — accepts the tip and settles nothing.
     * A bare node carries the tip signal but never moves value.
     */
    class NoopMeshTipSettlementProvider : MeshTipSettlementProvider {
        override suspend fun settleMeshTip(payload: TipPacketPayload) {
            // Intentionally does nothing.
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Builds, signs, and routes a [PacketType.TipPacket] (24) addressed to
     * [recipientUhid]. [amount] is the caller's input verbatim (the invariant
     * decimal string) — the protocol imposes NO policy on it. It is signed into the
     * payload and carried as-is. Returns the signed [MeshPacket] that was routed
     * onto the mesh.
     */
    suspend fun sendTip(
        recipientUhid: String,
        amount: String,
        trafficType: String,
        referenceId: java.util.UUID? = null,
        timestampUnixMs: Long,
    ): MeshPacket {
        var payload = TipPacketPayload(
            tipperUhid = sender.localUhid,
            recipientUhid = recipientUhid,
            amount = amount,
            trafficType = trafficType,
            referenceId = referenceId,
            timestampUnixMs = timestampUnixMs,
        )

        // Sign the payload's canonical bytes with the local identity key (real Ed25519).
        val sig = identity.signData(payload.buildCanonicalData())
        payload = payload.copy(signature = sig)

        val body = payload.toJson().toByteArray(Charsets.UTF_8)

        val packet = MeshPacket(
            type = PacketType.TipPacket,
            sourceUhid = sender.localUhid,
            destinationUhid = recipientUhid,
            ttl = DEFAULT_TTL,
            priority = 0,
            payload = body,
        )

        // Sign the enclosing MeshPacket (fills nonce / timestamp + envelope signature).
        signer.sign(packet)

        // Route toward the recipient: unicast over a discovered route, else broadcast.
        val nextHop = routing?.findNextHop(recipientUhid)
        if (nextHop != null) {
            sender.send(packet, nextHop)
            log.debug("MeshTip: sent (unicast) to recipient={} via {}", recipientUhid, nextHop)
            return packet
        }
        sender.broadcast(packet)
        log.debug("MeshTip: sent (broadcast) to recipient={}", recipientUhid)
        return packet
    }

    /**
     * Processes an inbound [PacketType.TipPacket] (24) received off the mesh.
     *
     * Returns true when the payload was accepted and handed to the settlement
     * provider. Returns false when the packet should be silently discarded (wrong
     * type, malformed payload, missing/malformed signature).
     */
    suspend fun handleTipPacket(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.TipPacket) {
            log.debug("MeshTip: unexpected packet type {} — ignored", packet.type)
            return false
        }

        // 1. Deserialise the payload. A malformed payload is logged and dropped.
        val payload = TipPacketPayload.fromJson(packet.payload.toString(Charsets.UTF_8))
        if (payload == null || payload.tipperUhid.isEmpty() || payload.recipientUhid.isEmpty()) {
            log.debug("MeshTip from {}: payload malformed or missing required fields — dropped", packet.sourceUhid)
            return false
        }

        // 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A
        //    payload carrying no signature, or a malformed one, is unverifiable —
        //    logged and dropped. The host's settlement provider is responsible for any
        //    stronger, key-bound verification it needs.
        if (payload.signature == null || payload.signature.size != ED25519_SIGNATURE_LENGTH) {
            log.debug("MeshTip from {}: missing or malformed signature — dropped", payload.tipperUhid)
            return false
        }

        // 3. Hand to the host's settlement provider. Default no-op settles nothing. A
        //    settlement error is logged but never breaks relaying.
        try {
            settle.settleMeshTip(payload)
        } catch (e: Exception) {
            log.warn("MeshTip from {}: settlement provider error", payload.tipperUhid, e)
        }

        // 4. Relay onward toward the addressed recipient if this node is not the
        //    destination and the packet may still be forwarded. The tip is ordinary
        //    addressed traffic.
        if (packet.destinationUhid != sender.localUhid && packet.canForward()) {
            val nextHop = routing?.findNextHop(packet.destinationUhid)
            if (nextHop != null) {
                sender.send(packet, nextHop)
            } else {
                sender.broadcast(packet)
            }
        }

        log.debug(
            "MeshTip handled: tipper={} recipient={} traffic={}",
            payload.tipperUhid, payload.recipientUhid, payload.trafficType,
        )
        return true
    }

    private companion object {
        private val log = LoggerFactory.getLogger(MeshTipService::class.java)

        /** `ProtocolConstants.DefaultTtl` — the default forward budget for a tip. */
        const val DEFAULT_TTL = 7

        /** Ed25519 signature length in bytes — used for the best-effort inbound check. */
        const val ED25519_SIGNATURE_LENGTH = 64
    }
}
