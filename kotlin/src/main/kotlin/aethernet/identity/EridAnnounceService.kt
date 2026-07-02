// SPDX-License-Identifier: MIT

// Wire binding for directed ERID announcements ([PacketType.EridAnnounce], 56). Binds
// the packet to the mesh: a node shares its rotating-address routing key with an
// established peer by sending the ALREADY Signal-encrypted announcement directly.
// Transport only — the plaintext framing ([EridAnnouncementCodec]) and the encryption
// (ISignalProtocolService) are done by the host/EridExchangeService; this service just
// carries the opaque encrypted blob as a directed packet and surfaces inbound ones via
// onAnnounceReceived. Port of the C# reference (EridAnnounceService).

package aethernet.identity

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender

/**
 * Binds [PacketType.EridAnnounce] (56) to the mesh: a node shares its rotating-address
 * routing key with an established peer by sending the (already Signal-encrypted)
 * announcement directly. Transport only — the plaintext framing
 * ([EridAnnouncementCodec]) and the encryption are the host's concern; this service just
 * carries the opaque encrypted blob as a directed packet and surfaces inbound ones via
 * [onAnnounceReceived] (bytes, fromUhid). Uses the in-memory [FakeMeshSender] in tests —
 * no transport needed. Mirrors the C# EridAnnounceService.
 */
class EridAnnounceService(private val sender: MeshSender) {

    /**
     * Raised when an ERID announcement arrives from a peer. First arg is the packet body
     * (still Signal-encrypted — its plaintext is an [EridAnnouncementCodec] frame); second
     * arg is the UHID of the peer that sent it.
     */
    var onAnnounceReceived: ((ByteArray, String) -> Unit)? = null

    /**
     * Send an [encrypted] ERID announcement directly to [peerUhid]. Returns delivery
     * success. Throws if [peerUhid] or [encrypted] is empty.
     */
    suspend fun sendAnnounce(peerUhid: String, encrypted: ByteArray): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        require(encrypted.isNotEmpty()) { "encrypted announcement must not be empty" }

        val packet = MeshPacket(
            type = PacketType.EridAnnounce,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = encrypted,
        )
        return sender.send(packet, peerUhid)
    }

    /**
     * Process an inbound [PacketType.EridAnnounce]. Returns false on wrong type or an
     * empty body; on success fires [onAnnounceReceived] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.EridAnnounce) return false
        if (packet.payload.isEmpty()) return false
        onAnnounceReceived?.invoke(packet.payload, packet.sourceUhid)
        return true
    }
}
