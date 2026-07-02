// SPDX-License-Identifier: MIT

package aethernet.prekey

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.security.PreKeyBundle
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Mesh pre-key exchange over [PacketType.PreKeyRequest] (25) and [PacketType.PreKeyResponse] (26).
 * Closes the "how does a peer get another peer's [PreKeyBundle] over the mesh" gap the messaging layer
 * previously left out-of-band.
 *
 * A node publishes its current bundle via [setLocalBundle] (the host produces it with
 * [aethernet.security.SignalProtocol.generatePreKeyBundle]). A peer asks for it with [requestBundle];
 * the responder replies with its bundle; the requester caches it and fires [onBundleReceived]. This
 * service is the mesh TRANSPORT of bundles — the host performs the actual X3DH by feeding the received
 * bundle to [aethernet.security.SignalProtocol.processPreKeyBundle] (Signal-canonical: no key agreement
 * happens here).
 *
 * Directed request/response — never broadcast — so bundle requests do not leak identity-interest to
 * the whole mesh. Mirrors the C# `PreKeyExchangeService` and the Kotlin
 * [aethernet.videocall.VideoCallControlService] (directed [MeshSender.send], nullable-lambda event).
 */
class PreKeyExchangeService(
    private val sender: MeshSender
) {
    /**
     * Raised when a peer's pre-key bundle arrives in a [PacketType.PreKeyResponse]. Nullable-lambda
     * event mechanism — matches the other Kotlin services and C# PreKeyExchangeService.BundleReceived.
     */
    var onBundleReceived: ((PreKeyBundleReceived) -> Unit)? = null

    private var local: PreKeyBundle? = null
    private val received = ConcurrentHashMap<String, PreKeyBundle>()

    /** Set (or replace) this node's published bundle — served in reply to inbound requests. */
    fun setLocalBundle(bundle: PreKeyBundle) {
        local = bundle
    }

    /** The currently-published local bundle, or null if none has been set. */
    fun getLocalBundle(): PreKeyBundle? = local

    /** The most recently received bundle for [uhid], or null. */
    fun getReceivedBundle(uhid: String): PreKeyBundle? = received[uhid]

    /**
     * Ask [peerUhid] for its pre-key bundle: mint a request id and send a directed
     * [PacketType.PreKeyRequest]. Returns the new request id (echoed by the response).
     */
    suspend fun requestBundle(peerUhid: String): UUID {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }

        val requestId = UUID.randomUUID()
        val payload = PreKeyRequestPayload(requestId = requestId, requesterUhid = sender.localUhid)
        val packet = MeshPacket(
            type = PacketType.PreKeyRequest,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes()
        )

        sender.send(packet, peerUhid)
        return requestId
    }

    /**
     * Process an incoming pre-key packet. On [PacketType.PreKeyRequest], reply with the local bundle
     * (if set). On [PacketType.PreKeyResponse], cache the peer bundle and fire [onBundleReceived].
     * Returns false for the wrong packet type, a malformed payload, or a request received when no
     * local bundle is set.
     */
    suspend fun handle(packet: MeshPacket): Boolean = when (packet.type) {
        PacketType.PreKeyRequest -> handleRequest(packet)
        PacketType.PreKeyResponse -> handleResponse(packet)
        else -> false
    }

    private suspend fun handleRequest(packet: MeshPacket): Boolean {
        val json = packet.payload.toString(Charsets.UTF_8)
        val body = PreKeyRequestPayload.fromJson(json) ?: return false

        val bundle = local ?: return false

        val replyTo = if (body.requesterUhid.isNotEmpty()) body.requesterUhid else packet.sourceUhid
        val payload = PreKeyResponsePayload.fromBundle(body.requestId, bundle)
        val reply = MeshPacket(
            type = PacketType.PreKeyResponse,
            sourceUhid = sender.localUhid,
            destinationUhid = replyTo,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes()
        )

        sender.send(reply, replyTo)
        return true
    }

    private fun handleResponse(packet: MeshPacket): Boolean {
        val json = packet.payload.toString(Charsets.UTF_8)
        val body = PreKeyResponsePayload.fromJson(json) ?: return false

        val bundle = body.toBundle()
        received[body.uhid] = bundle
        onBundleReceived?.invoke(
            PreKeyBundleReceived(
                requestId = body.requestId,
                fromUhid = packet.sourceUhid,
                bundle = bundle
            )
        )
        return true
    }
}
