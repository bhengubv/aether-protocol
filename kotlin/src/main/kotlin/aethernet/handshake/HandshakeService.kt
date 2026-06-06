// SPDX-License-Identifier: MIT

package aethernet.handshake

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import org.slf4j.LoggerFactory
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * Default capability handshake service. Tracks the peers we've Hello'd, the
 * peers we've finished negotiating with, and emits callbacks on completion /
 * incompatibility.
 *
 * Wire flow:
 *
 * ```
 * A → B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
 * A ← B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
 * ```
 *
 * Negotiation rules:
 *  - Negotiated version = `min(ourMax, theirMax)`.
 *  - If `min(ourMax,theirMax) < max(ourMin,theirMin)` the ranges do not
 *    overlap → fire [onIncompatiblePeer], refuse to lock in.
 *  - Locked-in capability set = `ourCaps ∩ theirCaps`.
 *
 * Mirrors `AetherNet.Handshake.HandshakeService` (C#) one-for-one. The JSON
 * shape on the wire is identical; cross-language interop is verified by C#
 * peers deserialising Kotlin-emitted Hello packets and vice versa.
 */
class HandshakeService(
    private val sender: MeshSender,
    private val ourMinVersion: Byte = 1,
    private val ourMaxVersion: Byte = AetherNetConstants.PROTOCOL_VERSION_CURRENT.toByte(),
    private val ourCapabilities: Set<String> = DEFAULT_CAPABILITIES,
    private val ourImplementation: String = DEFAULT_IMPLEMENTATION,
) {
    init {
        require(ourMinVersion <= ourMaxVersion) {
            "ourMinVersion ($ourMinVersion) cannot exceed ourMaxVersion ($ourMaxVersion)."
        }
    }

    private val logger = LoggerFactory.getLogger(HandshakeService::class.java)

    // Peers we've already sent a Hello to, to suppress duplicate sends.
    private val helloSent = ConcurrentHashMap.newKeySet<String>()

    // Peers we've finished negotiating with.
    private val negotiated = ConcurrentHashMap<String, PeerCapabilities>()

    /**
     * Invoked when negotiation completes (either via HelloAck receipt or via
     * the backward-compat fallback). Receives the locked-in [PeerCapabilities].
     */
    var onPeerNegotiated: ((PeerCapabilities) -> Unit)? = null

    /**
     * Invoked when a peer's announced version range does not overlap with
     * ours — we cannot speak to them. Subscribers should drop the peer from
     * their connected-peer set.
     */
    var onIncompatiblePeer: ((IncompatiblePeerEvent) -> Unit)? = null

    /**
     * Initiate a Hello toward a freshly-discovered peer. No-op if a Hello
     * has already been sent to this peer in the current session
     * (re-broadcasts can otherwise cause duplicate Hellos).
     */
    suspend fun initiate(peerUhid: String) {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        if (peerUhid == sender.localUhid) return

        // Suppress duplicate Hellos.
        if (!helloSent.add(peerUhid)) return

        val packet = buildPacket(PacketType.Hello, peerUhid)
        val delivered = sender.send(packet, peerUhid)
        logger.debug("Hello sent to {} delivered={}", peerUhid, delivered)
    }

    /**
     * Handle an inbound [PacketType.Hello]: lock in the announced capabilities
     * and reply with a HelloAck.
     */
    suspend fun handleHello(helloPacket: MeshPacket) {
        require(helloPacket.type == PacketType.Hello) {
            "Expected Hello, got ${helloPacket.type}"
        }
        if (helloPacket.sourceUhid.isEmpty()) return
        if (helloPacket.sourceUhid == sender.localUhid) return

        val theirs = HelloPayload.fromJsonBytesOrNull(helloPacket.payload)
        if (theirs == null) {
            logger.warn("Hello from {} has malformed payload — ignoring", helloPacket.sourceUhid)
            return
        }

        val negotiatedRecord = tryNegotiate(helloPacket.sourceUhid, theirs) ?: return

        negotiated[helloPacket.sourceUhid] = negotiatedRecord
        onPeerNegotiated?.invoke(negotiatedRecord)
        logger.info(
            "Hello accepted from {} → version={} caps=[{}] impl={}",
            helloPacket.sourceUhid,
            negotiatedRecord.negotiatedVersion,
            negotiatedRecord.capabilities.joinToString(","),
            negotiatedRecord.implementationVersion,
        )

        // Reply with HelloAck — even if we already sent them an unprompted
        // Hello, the spec is symmetric and the ack carries our own range / caps.
        val ack = buildPacket(PacketType.HelloAck, helloPacket.sourceUhid)
        val delivered = sender.send(ack, helloPacket.sourceUhid)
        logger.debug("HelloAck sent to {} delivered={}", helloPacket.sourceUhid, delivered)
    }

    /**
     * Handle an inbound [PacketType.HelloAck]: lock in the negotiated
     * capabilities for the replying peer.
     */
    fun handleHelloAck(helloAckPacket: MeshPacket) {
        require(helloAckPacket.type == PacketType.HelloAck) {
            "Expected HelloAck, got ${helloAckPacket.type}"
        }
        if (helloAckPacket.sourceUhid.isEmpty()) return
        if (helloAckPacket.sourceUhid == sender.localUhid) return

        val theirs = HelloPayload.fromJsonBytesOrNull(helloAckPacket.payload)
        if (theirs == null) {
            logger.warn("HelloAck from {} has malformed payload — ignoring", helloAckPacket.sourceUhid)
            return
        }

        val negotiatedRecord = tryNegotiate(helloAckPacket.sourceUhid, theirs) ?: return

        negotiated[helloAckPacket.sourceUhid] = negotiatedRecord
        onPeerNegotiated?.invoke(negotiatedRecord)
        logger.info(
            "HelloAck received from {} → version={} caps=[{}] impl={}",
            helloAckPacket.sourceUhid,
            negotiatedRecord.negotiatedVersion,
            negotiatedRecord.capabilities.joinToString(","),
            negotiatedRecord.implementationVersion,
        )
    }

    /**
     * Look up the locked-in capabilities for a peer. Returns null if the
     * handshake has not yet completed — callers can either wait for the
     * [onPeerNegotiated] callback or proceed with caution.
     */
    fun getPeerCapabilities(peerUhid: String): PeerCapabilities? {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        return negotiated[peerUhid]
    }

    /**
     * Drop a peer's cached capabilities and re-issue a Hello on the next
     * outbound contact. Used when version-mismatch is detected in subsequent
     * traffic.
     */
    fun renegotiate(peerUhid: String) {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        negotiated.remove(peerUhid)
        helloSent.remove(peerUhid)
        logger.info("Cleared cached capabilities for {}; next contact will re-Hello", peerUhid)
    }

    /**
     * Snapshot of every peer that has finished negotiating, for diagnostics
     * / health-check use.
     */
    fun getAllNegotiated(): List<PeerCapabilities> = negotiated.values.toList()

    /**
     * Backward-compat: install a "v1, no caps" record for a peer that never
     * replied to our Hello within the timeout window. Hosts call this from
     * their own timer / heartbeat loop. Idempotent — if the peer has since
     * replied with a HelloAck, the existing record wins.
     */
    fun assumeLegacyV1(peerUhid: String) {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        if (peerUhid == sender.localUhid) return

        val fallback = PeerCapabilities(
            peerUhid = peerUhid,
            negotiatedVersion = 1,
            capabilities = emptySet(),
            implementationVersion = "",
            negotiatedAt = Instant.now(),
        )

        // putIfAbsent — keep the existing record if a real HelloAck has
        // already populated one.
        val existing = negotiated.putIfAbsent(peerUhid, fallback)
        if (existing == null) {
            onPeerNegotiated?.invoke(fallback)
            logger.warn(
                "No HelloAck from {} after timeout — assuming protocol v1 / no advertised capabilities",
                peerUhid,
            )
        }
    }

    private fun buildPacket(type: PacketType, destinationUhid: String): MeshPacket {
        val payload = HelloPayload(
            minVersion = ourMinVersion,
            maxVersion = ourMaxVersion,
            capabilities = ourCapabilities.toList(),
            implementation = ourImplementation,
        )

        return MeshPacket(
            type = type,
            sourceUhid = sender.localUhid,
            destinationUhid = destinationUhid,
            ttl = 1, // direct hop only — handshake never relays
            priority = 0,
            protocolVersion = ourMaxVersion,
            payload = payload.toJsonBytes(),
        )
    }

    /**
     * Returns the negotiated record on success, or null and fires
     * [onIncompatiblePeer] on version-overlap failure.
     */
    private fun tryNegotiate(peerUhid: String, theirs: HelloPayload): PeerCapabilities? {
        if (theirs.minVersion > theirs.maxVersion) {
            logger.warn(
                "Handshake from {} announces inverted range min={} > max={} — refusing",
                peerUhid, theirs.minVersion, theirs.maxVersion,
            )
            fireIncompatible(peerUhid, theirs, "inverted version range")
            return null
        }

        // Overlap check: highest min must be ≤ lowest max.
        val overlapMin = maxOf(ourMinVersion.toInt(), theirs.minVersion.toInt())
        val overlapMax = minOf(ourMaxVersion.toInt(), theirs.maxVersion.toInt())
        if (overlapMin > overlapMax) {
            fireIncompatible(
                peerUhid, theirs,
                "no version overlap (ours=$ourMinVersion..$ourMaxVersion, " +
                    "theirs=${theirs.minVersion}..${theirs.maxVersion})",
            )
            return null
        }

        // Pick the highest mutually-supported version.
        val chosenVersion = overlapMax.toByte()

        // Capability intersection (case-sensitive — capability names are
        // wire constants, not human strings).
        val intersection = LinkedHashSet<String>()
        for (cap in theirs.capabilities) {
            if (cap.isNotEmpty() && ourCapabilities.contains(cap)) {
                intersection.add(cap)
            }
        }

        return PeerCapabilities(
            peerUhid = peerUhid,
            negotiatedVersion = chosenVersion,
            capabilities = intersection,
            implementationVersion = theirs.implementation,
            negotiatedAt = Instant.now(),
        )
    }

    private fun fireIncompatible(peerUhid: String, theirs: HelloPayload, reason: String) {
        logger.warn("Incompatible peer {}: {}", peerUhid, reason)
        onIncompatiblePeer?.invoke(
            IncompatiblePeerEvent(
                peerUhid = peerUhid,
                theirMinVersion = theirs.minVersion,
                theirMaxVersion = theirs.maxVersion,
                ourMinVersion = ourMinVersion,
                ourMaxVersion = ourMaxVersion,
                reason = reason,
            ),
        )
    }

    companion object {
        /** Default capability tags advertised by this implementation. */
        val DEFAULT_CAPABILITIES: Set<String> = linkedSetOf(
            "signal-x3dh",
            "double-ratchet",
            "dtn-custody",
            "sos",
            "voice",
            "stream",
        )

        /** Default implementation banner emitted in our Hello / HelloAck. */
        const val DEFAULT_IMPLEMENTATION: String = "aether-kotlin/1.0.0"
    }
}
