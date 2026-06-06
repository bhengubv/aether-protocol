// SPDX-License-Identifier: MIT

package aethermesh.sos

import aethermesh.AetherMeshConstants
import aethermesh.extensibility.BackendClient
import aethermesh.extensibility.IncentiveProvider
import aethermesh.extensibility.NoopBackendClient
import aethermesh.extensibility.NoopIncentiveProvider
import aethermesh.models.SosAlert
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import aethermesh.routing.MeshSender
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
            if (recentOrigins.size >= AetherMeshConstants.MAX_SOS_BROADCASTS_PER_HOUR) return false
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
            ttl = AetherMeshConstants.SOS_TTL,
            priority = AetherMeshConstants.SOS_PRIORITY.toByte(),
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

        // Wire decoding kept minimal here — see DtnService note. Hosts wire up a JSON
        // library on receive side; for now we surface the alert with packet metadata only.
        val alert = SosAlert(
            senderUhid = packet.sourceUhid,
            broadcastType = "sos",
            message = null,
            latitude = 0.0,
            longitude = 0.0,
            geohash = null
        )
        activeAlerts[alert.id] = alert
        onSosReceived?.invoke(alert)

        if (packet.ttl > 1) {
            packet.ttl -= 1
            sender.broadcast(packet)
            incentives.recordRelay(sender.localUhid, packet)
        }
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
