// SPDX-License-Identifier: MIT
package aethernet

import aethernet.models.PeerInfo
import aethernet.protocol.MeshPacket
import aethernet.routing.MeshSender
import java.util.Collections

/** Records a single unicast send from FakeMeshSender. */
data class UnicastRecord(val packet: MeshPacket, val nextHopUhid: String)

/**
 * In-memory MeshSender for unit tests.
 *
 *  - `connectedPeers` returns peers added via [addPeer]
 *  - `send` records a [UnicastRecord] and returns true unless the peer is in the
 *    fail-set, in which case it records nothing and returns false
 *  - `broadcast` records the packet and returns the connected-peer count
 *
 * Defensively clones every recorded packet so subsequent test mutations of the
 * original don't corrupt the recorded snapshot.
 */
class FakeMeshSender(
    override val localUhid: String,
    override val localGeohash: String? = null,
) : MeshSender {

    private val peers: MutableList<PeerInfo> = Collections.synchronizedList(mutableListOf())
    private val failTo: MutableSet<String> = Collections.synchronizedSet(mutableSetOf())
    private val unicastsList: MutableList<UnicastRecord> = Collections.synchronizedList(mutableListOf())
    private val broadcastsList: MutableList<MeshPacket> = Collections.synchronizedList(mutableListOf())

    val unicasts: List<UnicastRecord> get() = synchronized(unicastsList) { unicastsList.toList() }
    val broadcasts: List<MeshPacket> get() = synchronized(broadcastsList) { broadcastsList.toList() }

    fun addPeer(peer: PeerInfo) { peers.add(peer) }
    fun failSendsTo(uhid: String) { failTo.add(uhid) }
    fun clear() {
        synchronized(unicastsList) { unicastsList.clear() }
        synchronized(broadcastsList) { broadcastsList.clear() }
    }

    override fun connectedPeers(): List<PeerInfo> = synchronized(peers) { peers.toList() }

    override suspend fun send(packet: MeshPacket, nextHopUhid: String): Boolean {
        if (nextHopUhid in failTo) return false
        unicastsList.add(UnicastRecord(clone(packet), nextHopUhid))
        return true
    }

    override suspend fun broadcast(packet: MeshPacket): Int {
        broadcastsList.add(clone(packet))
        return synchronized(peers) { peers.size }
    }

    private fun clone(p: MeshPacket): MeshPacket = p.copy(
        payload = p.payload.copyOf(),
        signature = p.signature.copyOf(),
        packetNonce = p.packetNonce.copyOf(),
    )
}
