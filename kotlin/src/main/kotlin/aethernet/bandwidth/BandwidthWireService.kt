// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * A latency/throughput probe request ([PacketType.BandwidthProbe] = 53 body).
 *
 * [sequence] is an unsigned 32-bit counter; it is carried on the wire as a raw
 * little-endian u32. [senderSendUs] is the prober's local send timestamp in
 * microseconds since the Unix epoch. Mirrors the C# `BandwidthProbe` record.
 */
data class BandwidthProbe(val sequence: UInt, val senderSendUs: Long)

/**
 * An inbound probe plus the peer that sent it, so the host can reply with an ack.
 * Mirrors the C# `BandwidthProbeReceived` event args.
 */
data class BandwidthProbeReceived(val probe: BandwidthProbe, val fromUhid: String)

/**
 * Binary wire codec for the three ABMF packets (AetherNet Bandwidth Measurement
 * Framework — ABMF W18-5). All multi-byte integers are LITTLE-ENDIAN, matching the
 * packet-serializer convention. NO version byte — the layouts are the ones documented
 * on the [PacketType] members. Byte-identity gate: `fixtures/bandwidth/vectors.json` (hex).
 *
 *   Probe(53)  : sequence u32 | sender_send_us i64                                                        (12 B)
 *   Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
 *   Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                            (13 B)
 *
 * [BandwidthProbeAck.senderReceiveUs] is NOT on the wire — the prober fills it locally
 * on receipt (0 on deserialize). [BandwidthGossipPayload.peerUhid]/[BandwidthGossipPayload.transportName]/
 * [BandwidthGossipPayload.measuredAt] come from the enclosing packet + local clock, not the wire body.
 *
 * Direct mirror of the C# `BandwidthWireCodec`.
 */
object BandwidthWireCodec {

    private const val PROBE_LEN = 12
    private const val ACK_LEN = 32
    private const val GOSSIP_LEN = 13

    private fun buffer(size: Int): ByteBuffer =
        ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)

    private fun reader(b: ByteArray): ByteBuffer =
        ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)

    // ── Probe ───────────────────────────────────────────────────────────────

    fun serializeProbe(p: BandwidthProbe): ByteArray =
        buffer(PROBE_LEN)
            .putInt(p.sequence.toInt())   // u32 written as raw 4 LE bytes
            .putLong(p.senderSendUs)
            .array()

    fun deserializeProbe(b: ByteArray): BandwidthProbe {
        require(b.size >= PROBE_LEN) { "BandwidthProbe payload too short" }
        val r = reader(b)
        return BandwidthProbe(
            sequence = r.int.toUInt(),    // raw 4 LE bytes read back masked to unsigned
            senderSendUs = r.long,
        )
    }

    // ── Ack ─────────────────────────────────────────────────────────────────

    fun serializeAck(a: BandwidthProbeAck): ByteArray =
        buffer(ACK_LEN)
            .putInt(a.sequence.toInt())
            .putLong(a.senderSendUs)
            .putLong(a.receiverReceiveUs)
            .putLong(a.receiverSendUs)
            // senderReceiveUs is local-only — deliberately NOT serialized.
            .putInt(a.probeBytes)
            .array()

    fun deserializeAck(b: ByteArray): BandwidthProbeAck {
        require(b.size >= ACK_LEN) { "BandwidthProbeAck payload too short" }
        val r = reader(b)
        val sequence = r.int.toUInt()
        val senderSendUs = r.long
        val receiverReceiveUs = r.long
        val receiverSendUs = r.long
        val probeBytes = r.int
        return BandwidthProbeAck(
            sequence = sequence,
            senderSendUs = senderSendUs,
            receiverReceiveUs = receiverReceiveUs,
            receiverSendUs = receiverSendUs,
            senderReceiveUs = 0L, // filled by the prober on receipt, not carried on the wire
            probeBytes = probeBytes,
        )
    }

    // ── Gossip ──────────────────────────────────────────────────────────────

    fun serializeGossip(g: BandwidthGossipPayload): ByteArray =
        buffer(GOSSIP_LEN)
            .putLong(g.btlBwBps)
            .putInt(g.rtPropUs.coerceIn(0L, Int.MAX_VALUE.toLong()).toInt()) // i64 model → clamped i32 wire
            .put(g.confidence.ordinal.toByte())                              // None=0/Low=1/Medium=2/High=3
            .array()

    /**
     * Decode a gossip body. [BandwidthGossipPayload.peerUhid]/[BandwidthGossipPayload.transportName]
     * default to empty; the service fills [BandwidthGossipPayload.peerUhid] from the packet source.
     */
    fun deserializeGossip(b: ByteArray): BandwidthGossipPayload {
        require(b.size >= GOSSIP_LEN) { "BandwidthGossipPayload payload too short" }
        val r = reader(b)
        val btlBwBps = r.long
        val rtPropUs = r.int
        val confidence = confidenceFromOrdinal(r.get().toInt() and 0xff)
        return BandwidthGossipPayload(
            peerUhid = "",
            transportName = "",
            btlBwBps = btlBwBps,
            rtPropUs = rtPropUs.toLong(),
            confidence = confidence,
            measuredAt = java.time.Instant.EPOCH,
        )
    }

    private fun confidenceFromOrdinal(o: Int): BandwidthConfidence = when (o) {
        0 -> BandwidthConfidence.NONE
        1 -> BandwidthConfidence.LOW
        2 -> BandwidthConfidence.MEDIUM
        3 -> BandwidthConfidence.HIGH
        else -> throw IllegalArgumentException("BandwidthGossipPayload: invalid confidence ordinal $o")
    }
}

/**
 * Binds the three ABMF PacketTypes to the mesh: send probes (directed) + their acks (directed reply),
 * and broadcast/receive warm-start gossip. Inbound packets surface via nullable-lambda callbacks; the
 * host feeds them into `BandwidthEstimator` (recordProbeResult / warmFromGossip) and replies to probes.
 *
 * Callbacks mirror the C# events ([onProbeReceived]/[onAckReceived]/[onGossipReceived]); the Kotlin
 * idiom is a settable nullable lambda (see [aethernet.heartbeat.HeartbeatService.onPeerSeen]).
 * Direct mirror of the C# `BandwidthWireService`.
 */
class BandwidthWireService(
    private val sender: MeshSender,
) {
    /** Raised when a [PacketType.BandwidthProbe] is received (probe body + source peer). */
    var onProbeReceived: ((BandwidthProbeReceived) -> Unit)? = null

    /** Raised when a [PacketType.BandwidthAck] is received. */
    var onAckReceived: ((BandwidthProbeAck) -> Unit)? = null

    /** Raised when a [PacketType.BandwidthGossip] is received (peerUhid filled from the packet source). */
    var onGossipReceived: ((BandwidthGossipPayload) -> Unit)? = null

    /** Send a directed [PacketType.BandwidthProbe] to a peer. */
    suspend fun sendProbe(peerUhid: String, probe: BandwidthProbe): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        return sendDirected(peerUhid, PacketType.BandwidthProbe, BandwidthWireCodec.serializeProbe(probe))
    }

    /** Send a directed [PacketType.BandwidthAck] reply to the prober. */
    suspend fun sendAck(peerUhid: String, ack: BandwidthProbeAck): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        return sendDirected(peerUhid, PacketType.BandwidthAck, BandwidthWireCodec.serializeAck(ack))
    }

    private suspend fun sendDirected(peerUhid: String, type: PacketType, payload: ByteArray): Boolean {
        val packet = MeshPacket(
            type = type,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload,
        )
        return sender.send(packet, peerUhid)
    }

    /** Broadcast a [PacketType.BandwidthGossip] warm-start estimate. Returns peers reached. */
    suspend fun broadcastGossip(gossip: BandwidthGossipPayload): Int {
        val packet = MeshPacket(
            type = PacketType.BandwidthGossip,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = BandwidthWireCodec.serializeGossip(gossip),
        )
        return sender.broadcast(packet)
    }

    /**
     * Dispatch an inbound bandwidth packet to the matching callback. Returns false on wrong type or a
     * malformed body; true when a callback fired for a recognized, well-formed packet.
     */
    fun handle(packet: MeshPacket): Boolean {
        return try {
            when (packet.type) {
                PacketType.BandwidthProbe -> {
                    val probe = BandwidthWireCodec.deserializeProbe(packet.payload)
                    onProbeReceived?.invoke(BandwidthProbeReceived(probe, packet.sourceUhid))
                    true
                }

                PacketType.BandwidthAck -> {
                    val ack = BandwidthWireCodec.deserializeAck(packet.payload)
                    onAckReceived?.invoke(ack)
                    true
                }

                PacketType.BandwidthGossip -> {
                    val gossip = BandwidthWireCodec.deserializeGossip(packet.payload)
                        .copy(peerUhid = packet.sourceUhid)
                    onGossipReceived?.invoke(gossip)
                    true
                }

                else -> false
            }
        } catch (_: IllegalArgumentException) {
            // Malformed / truncated body — drop the packet (mirrors C# FormatException catch).
            false
        }
    }
}
