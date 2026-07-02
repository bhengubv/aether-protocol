// SPDX-License-Identifier: MIT

package aethernet.videocall

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.UUID

/**
 * Video call-control over [PacketType.VideoCall] — directed ring/accept/decline/hangup signalling
 * between two peers. The caller rings a peer (minting a call id); either side then accepts, declines,
 * or hangs up. Inbound signals surface via [onCallStateChanged]. The media plane (SDP/ICE + frames)
 * is handled separately by the streaming VideoCall service.
 *
 * Mirrors the C# `VideoCallControlService` and the Kotlin [aethernet.sos.SosBroadcastService]
 * (directed [MeshSender.send]) / [aethernet.channels.ChannelMessageService] (nullable-lambda event).
 */
class VideoCallControlService(
    private val sender: MeshSender
) {
    /**
     * Raised when a call-control signal is received from a peer. Nullable-lambda event mechanism —
     * matches the other Kotlin services and C# VideoCallControlService.CallStateChanged.
     */
    var onCallStateChanged: ((VideoCallStateChanged) -> Unit)? = null

    /** Ring [peerUhid]: mint a call id and send a directed "ring". Returns the new call id. */
    suspend fun ring(peerUhid: String): UUID {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        val callId = UUID.randomUUID()
        sendControl(callId, peerUhid, "ring")
        return callId
    }

    /** Send a directed "accept" for [callId] to [peerUhid]. Returns delivery success. */
    suspend fun accept(callId: UUID, peerUhid: String): Boolean = sendControl(callId, peerUhid, "accept")

    /** Send a directed "decline" for [callId] to [peerUhid]. Returns delivery success. */
    suspend fun decline(callId: UUID, peerUhid: String): Boolean = sendControl(callId, peerUhid, "decline")

    /** Send a directed "hangup" for [callId] to [peerUhid]. Returns delivery success. */
    suspend fun hangup(callId: UUID, peerUhid: String): Boolean = sendControl(callId, peerUhid, "hangup")

    private suspend fun sendControl(callId: UUID, peerUhid: String, action: String): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }

        val payload = VideoCallControlPayload(
            callId = callId,
            action = action,
            sentAtMs = Instant.now().toEpochMilli()
        )

        val packet = MeshPacket(
            type = PacketType.VideoCall,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes()
        )

        return sender.send(packet, peerUhid)
    }

    /**
     * Process an incoming [PacketType.VideoCall] packet: parse and raise [onCallStateChanged].
     * Returns false for the wrong packet type or a malformed payload (missing/empty action).
     */
    fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.VideoCall) return false

        val json = packet.payload.toString(Charsets.UTF_8)
        val callId = JsonReader.readString(json, "call_id")?.let {
            runCatching { UUID.fromString(it) }.getOrNull()
        } ?: return false
        val action = JsonReader.readString(json, "action")
        if (action.isNullOrEmpty()) return false

        onCallStateChanged?.invoke(
            VideoCallStateChanged(
                callId = callId,
                action = action,
                fromUhid = packet.sourceUhid
            )
        )
        return true
    }
}
