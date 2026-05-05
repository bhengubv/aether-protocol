// SPDX-License-Identifier: MIT

package aether.routing

import aether.models.PeerInfo
import aether.protocol.MeshPacket

/**
 * Minimal sending abstraction for routing/DTN/SOS. Hosts wire this with a thin
 * adapter over their transport so the protocol services do not take a hard
 * dependency on a specific transport implementation.
 */
interface MeshSender {
    /** The local node's UHID. Used as packet.sourceUhid on outbound packets. */
    val localUhid: String

    /** Local node's last-known geohash, or null if not shared. */
    val localGeohash: String? get() = null

    /** Snapshot of currently directly-connected peers. */
    fun connectedPeers(): List<PeerInfo> = emptyList()

    /** Forward a packet to a single next-hop peer. Returns true if delivered. */
    suspend fun send(packet: MeshPacket, nextHopUhid: String): Boolean

    /** Broadcast a packet to every connected peer. Returns the fan-out count. */
    suspend fun broadcast(packet: MeshPacket): Int
}
