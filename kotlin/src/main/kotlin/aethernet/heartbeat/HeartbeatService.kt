// SPDX-License-Identifier: MIT

package aethernet.heartbeat

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Broadcasts and handles [PacketType.Heartbeat] liveness beacons. A node periodically emits a
 * heartbeat to its direct neighbours (TTL 1); receivers maintain a per-peer [PeerLiveness] table
 * (keyed by source UHID) and can query which peers are currently live.
 *
 * Unauthenticated by design — like SOS, a heartbeat is a low-stakes liveness hint, not a security
 * assertion. Mirrors the C# `HeartbeatService` and the Kotlin [aethernet.sos.SosBroadcastService].
 */
class HeartbeatService(
    private val sender: MeshSender
) {
    private val sequence = AtomicInteger(0)
    private val peers = ConcurrentHashMap<String, PeerLiveness>()

    /** Raised when a heartbeat is received from a peer (new or refreshed liveness). */
    var onPeerSeen: ((PeerLiveness) -> Unit)? = null

    /**
     * Broadcast a single heartbeat to all directly connected peers (TTL 1). The sequence number
     * increments on every call. Returns the number of peers the beacon was delivered to.
     */
    suspend fun sendHeartbeat(): Int {
        val seq = sequence.incrementAndGet()
        val payload = HeartbeatPayload(
            sequence = seq,
            sentAtMs = Instant.now().toEpochMilli()
        ).toJsonBytes()

        val packet = MeshPacket(
            type = PacketType.Heartbeat,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = 1, // heartbeats are single-hop: liveness of DIRECT neighbours only
            payload = payload
        )

        return sender.broadcast(packet)
    }

    /**
     * Process an incoming [PacketType.Heartbeat] packet: refresh the sender's liveness record
     * (keyed by source UHID) and fire [onPeerSeen]. Returns false (no-op) for the wrong packet
     * type, self-originated heartbeats, or a malformed payload; true when a peer was recorded.
     */
    fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.Heartbeat) return false

        // Ignore our own heartbeat echoed back.
        if (packet.sourceUhid == sender.localUhid) return false

        val json = packet.payload.toString(Charsets.UTF_8)
        val seq = JsonReader.readInt(json, "sequence") ?: return false
        val sentAtMs = JsonReader.readLong(json, "sent_at_ms") ?: return false

        val liveness = PeerLiveness(
            uhid = packet.sourceUhid,
            lastSequence = seq,
            lastSentAtMs = sentAtMs,
            receivedAtMs = Instant.now().toEpochMilli()
        )
        peers[packet.sourceUhid] = liveness
        onPeerSeen?.invoke(liveness)
        return true
    }

    /** Snapshot of every peer this node has ever seen a heartbeat from. */
    fun getKnownPeers(): List<PeerLiveness> = peers.values.toList()

    /** Peers whose most recent heartbeat was received within the last [withinSeconds] seconds. */
    fun getLivePeers(withinSeconds: Int): List<PeerLiveness> {
        val cutoff = Instant.now().toEpochMilli() - withinSeconds.toLong() * 1000L
        return peers.values.filter { it.receivedAtMs >= cutoff }
    }
}
