// SPDX-License-Identifier: MIT

package aethernet.sos

import aethernet.AetherNetConstants
import aethernet.extensibility.BackendClient
import aethernet.extensibility.IncentiveProvider
import aethernet.extensibility.NoopBackendClient
import aethernet.extensibility.NoopIncentiveProvider
import aethernet.models.SosAcknowledgement
import aethernet.models.SosAlert
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * SOS broadcast service. Originates and re-floods SOS broadcasts.
 *
 * Dedups by packet ID; rate-limited to MAX_SOS_BROADCASTS_PER_HOUR per rolling hour.
 */
class SosBroadcastService(
    private val sender: MeshSender,
    private val backend: BackendClient = NoopBackendClient(),
    private val incentives: IncentiveProvider = NoopIncentiveProvider()
) {
    private val recentOrigins = ArrayDeque<Instant>()
    private val seen = ConcurrentHashMap.newKeySet<UUID>()
    private val activeAlerts = ConcurrentHashMap<UUID, SosAlert>()

    var onSosReceived: ((SosAlert) -> Unit)? = null
    var onSosResolved: ((UUID) -> Unit)? = null

    /**
     * Raised on the ORIGINATING node when a peer acknowledges receipt of one of its active SOS
     * alerts. Mirrors the other Kotlin SOS events (nullable-lambda mechanism) and C#
     * SosBroadcastService.SosAcknowledged.
     */
    var onSosAcknowledged: ((SosAcknowledgement) -> Unit)? = null

    suspend fun broadcast(
        broadcastType: String,
        message: String?,
        latitude: Double,
        longitude: Double,
        geohash: String? = null
    ): Boolean {
        require(broadcastType.isNotEmpty()) { "broadcastType must not be empty" }

        synchronized(recentOrigins) {
            pruneOldOrigins()
            if (recentOrigins.size >= AetherNetConstants.MAX_SOS_BROADCASTS_PER_HOUR) return false
            recentOrigins.addLast(Instant.now())
        }

        val alert = SosAlert(
            senderUhid = sender.localUhid,
            broadcastType = broadcastType,
            message = message,
            latitude = latitude,
            longitude = longitude,
            geohash = geohash
        )
        activeAlerts[alert.id] = alert

        val body = encodeSosWire(alert.id, broadcastType, message, latitude, longitude, geohash)

        val packet = MeshPacket(
            type = PacketType.SosBroadcast,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.SOS_TTL,
            priority = AetherNetConstants.SOS_PRIORITY.toByte(),
            payload = body
        )
        seen.add(packet.id)

        sender.broadcast(packet)
        backend.syncSos(alert)
        return true
    }

    fun resolve(broadcastId: UUID) {
        if (activeAlerts.remove(broadcastId) != null) {
            onSosResolved?.invoke(broadcastId)
        }
    }

    fun getActiveAlerts(): List<SosAlert> = activeAlerts.values.toList()

    suspend fun handle(packet: MeshPacket) {
        require(packet.type == PacketType.SosBroadcast) { "expected PacketType.SosBroadcast" }
        if (!seen.add(packet.id)) return

        if (packet.sourceUhid == sender.localUhid) return

        // Decode the cleartext SOS envelope from the payload (broadcast_type / message /
        // latitude / longitude / geohash) via the shared JsonReader — matches the C#
        // reference. An SOS must carry its message and GPS fix, not just packet headers.
        val json = packet.payload.toString(Charsets.UTF_8)
        // Preserve the originator's broadcast id from the envelope so the directed ack references
        // the same alert the originator holds in its activeAlerts — matches the C# reference.
        val broadcastId = JsonReader.readString(json, "broadcast_id")?.let {
            runCatching { UUID.fromString(it) }.getOrNull()
        } ?: UUID.randomUUID()
        val alert = SosAlert(
            id = broadcastId,
            senderUhid = packet.sourceUhid,
            broadcastType = JsonReader.readString(json, "broadcast_type") ?: "sos",
            message = JsonReader.readString(json, "message"),
            latitude = JsonReader.readDouble(json, "latitude") ?: 0.0,
            longitude = JsonReader.readDouble(json, "longitude") ?: 0.0,
            geohash = JsonReader.readString(json, "geohash")
        )
        activeAlerts[alert.id] = alert
        onSosReceived?.invoke(alert)

        // Acknowledge back to the originator so the sender learns their SOS reached a device.
        sendSosAck(alert.id, packet.sourceUhid)

        if (packet.ttl > 1) {
            packet.ttl -= 1
            sender.broadcast(packet)
            incentives.recordRelay(sender.localUhid, packet)
        }
    }

    /**
     * Handle an inbound [PacketType.SosAck] on the ORIGINATING node. Parses the payload, finds the
     * active alert this node originated (no-op if not found — every non-originator ignores the ack),
     * ignores our own echoed ack, dedups by responder uhid, and on a new distinct responder records
     * it and emits [onSosAcknowledged] with the running distinct total. The responder's identity is
     * the ack packet's SOURCE uhid, not carried in the payload. Mirrors C# HandleAckAsync.
     */
    fun handleAck(ackPacket: MeshPacket) {
        require(ackPacket.type == PacketType.SosAck) { "expected PacketType.SosAck" }

        val json = ackPacket.payload.toString(Charsets.UTF_8)
        val broadcastId = JsonReader.readString(json, "broadcast_id")?.let {
            runCatching { UUID.fromString(it) }.getOrNull()
        } ?: return

        // Only the ORIGINATOR holds this alert in activeAlerts; every other node ignores the ack.
        val alert = activeAlerts[broadcastId] ?: return

        val responder = ackPacket.sourceUhid
        if (responder.isEmpty()) return
        if (responder == sender.localUhid) return // our own ack echoed back — ignore

        val total: Int
        synchronized(alert.acknowledgedBy) {
            if (!alert.acknowledgedBy.add(responder)) return // already counted — dedup
            total = alert.acknowledgedBy.size
        }

        onSosAcknowledged?.invoke(
            SosAcknowledgement(
                broadcastId = broadcastId,
                responderUhid = responder,
                totalAcknowledgements = total
            )
        )
    }

    /**
     * Send a directed [PacketType.SosAck] back to the alert originator so the sender learns their
     * emergency reached this device. Best-effort: delivers when the originator is reachable as a
     * next hop via the sender's directed send. Mirrors C# SendSosAckAsync.
     */
    private suspend fun sendSosAck(broadcastId: UUID, originatorUhid: String) {
        if (originatorUhid.isEmpty()) return
        if (originatorUhid == sender.localUhid) return

        val payload = SosAckPayload(
            broadcastId = broadcastId,
            receivedAtMs = Instant.now().toEpochMilli()
        ).toJsonBytes()

        val ack = MeshPacket(
            type = PacketType.SosAck,
            sourceUhid = sender.localUhid,
            destinationUhid = originatorUhid,
            ttl = AetherNetConstants.SOS_TTL,
            priority = AetherNetConstants.SOS_PRIORITY.toByte(),
            payload = payload
        )

        sender.send(ack, originatorUhid)
    }

    private fun pruneOldOrigins() {
        val cutoff = Instant.now().minusSeconds(3600)
        while (recentOrigins.isNotEmpty() && recentOrigins.first().isBefore(cutoff)) {
            recentOrigins.removeFirst()
        }
    }

    private fun encodeSosWire(
        broadcastId: UUID,
        broadcastType: String,
        message: String?,
        latitude: Double,
        longitude: Double,
        geohash: String?
    ): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"broadcast_id\":\"").append(broadcastId).append("\",")
        sb.append("\"broadcast_type\":\"").append(jsonEscape(broadcastType)).append("\",")
        sb.append("\"message\":")
        if (message == null) sb.append("null") else sb.append('"').append(jsonEscape(message)).append('"')
        sb.append(',')
        sb.append("\"latitude\":").append(latitude).append(',')
        sb.append("\"longitude\":").append(longitude).append(',')
        sb.append("\"geohash\":")
        if (geohash == null) sb.append("null") else sb.append('"').append(jsonEscape(geohash)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    private fun jsonEscape(s: String): String {
        val sb = StringBuilder()
        for (c in s) {
            when (c) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> sb.append(c)
            }
        }
        return sb.toString()
    }
}
