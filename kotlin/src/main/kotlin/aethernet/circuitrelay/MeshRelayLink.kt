// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * Production [RelayLink] that carries circuit-relay-v2 frames one hop over the real mesh —
 * mirrors the C# `MeshRelayLink` and the Go / Python / TS / Rust `MeshRelayLink`.
 *
 * Each frame is wrapped in a [MeshPacket] of type [PacketType.CircuitRelayControl] and handed
 * to the host's send-to-connected-peer function; inbound CircuitRelayControl packets are fed
 * back into the engine via [handleIncomingPacket]. The two functions are the seam to whatever
 * real transport the host runs (BLE / Wi-Fi Direct / WebRTC / the HTTP relay). It never calls
 * a radio directly and never recurses through itself (the host's one-hop send must exclude the
 * circuit-relay transport).
 *
 * @param localUhid this node's UHID (stamped as the packet source).
 * @param sendOneHop sends a MeshPacket to a directly-connected peer; `true` if handed off.
 * @param canReachFn reports whether this node has a direct one-hop link to a peer.
 */
class MeshRelayLink(
    private val localUhid: String,
    private val sendOneHop: (MeshPacket) -> Boolean,
    private val canReachFn: (String) -> Boolean
) : RelayLink {
    private val lock = ReentrantLock()
    private var handler: ((String, ByteArray) -> Unit)? = null

    override fun sendFrame(node: String, frame: ByteArray): Boolean {
        val pkt = MeshPacket(
            type = PacketType.CircuitRelayControl,
            sourceUhid = localUhid,
            destinationUhid = node,
            payload = frame,
            ttl = 1 // relay frames travel exactly one hop; end-to-end routing is the engine's job
        )
        return sendOneHop(pkt)
    }

    override fun canReach(node: String): Boolean = canReachFn(node)

    override fun onFrame(handler: (String, ByteArray) -> Unit) {
        lock.withLock { this.handler = handler }
    }

    /**
     * Feeds an inbound CircuitRelayControl packet from the host's receive path into the relay
     * engine (non-relay packet types are ignored). The host must call this for every received
     * [PacketType.CircuitRelayControl] packet.
     */
    fun handleIncomingPacket(packet: MeshPacket) {
        if (packet.type != PacketType.CircuitRelayControl) return
        val h = lock.withLock { handler }
        h?.invoke(packet.sourceUhid, packet.payload)
    }
}
