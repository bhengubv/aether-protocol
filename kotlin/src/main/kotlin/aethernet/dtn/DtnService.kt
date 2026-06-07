// SPDX-License-Identifier: MIT

package aethernet.dtn

import aethernet.AetherNetConstants
import aethernet.extensibility.BackendClient
import aethernet.extensibility.IncentiveProvider
import aethernet.extensibility.NoopBackendClient
import aethernet.extensibility.NoopIncentiveProvider
import aethernet.models.BundlePriority
import aethernet.models.CustodyRecord
import aethernet.models.DtnBundle
import aethernet.models.DtnBundleReceivedEvent
import aethernet.models.DtnDeliveryReceipt
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.security.NodeReputationService
import java.time.Instant
import java.util.UUID

/**
 * Default DTN service. Three-tier delivery:
 *   direct mesh send → DTN epidemic replication → backend relay.
 */
class DtnService(
    private val sender: MeshSender,
    private val store: BundleStore = InMemoryBundleStore(),
    private val strategy: ReplicationStrategy = GeohashEpidemicStrategy(),
    private val incentives: IncentiveProvider = NoopIncentiveProvider(),
    private val backend: BackendClient = NoopBackendClient()
) {
    @Volatile private var reputation: NodeReputationService? = null

    fun setReputation(rep: NodeReputationService?) { reputation = rep }

    var onBundleDelivered: ((DtnDeliveryReceipt) -> Unit)? = null

    /**
     * Fires the moment a DTN bundle arrives whose final recipient is the local
     * node. Added in v1.2.0 - closes the Wave-16 gap surfaced by Issue #59.
     */
    var onBundleReceived: ((DtnBundleReceivedEvent) -> Unit)? = null

    suspend fun createBundle(
        recipientUhid: String,
        encryptedPayload: ByteArray,
        priority: BundlePriority = BundlePriority.Normal,
        recipientLastGeohash: String? = null
    ): DtnBundle {
        require(recipientUhid.isNotEmpty()) { "recipientUhid must not be empty" }
        var bundle = DtnBundle(
            senderUhid = sender.localUhid,
            recipientUhid = recipientUhid,
            encryptedPayload = encryptedPayload,
            priority = priority.value,
            status = "Pending",
            senderGeohash = sender.localGeohash,
            recipientLastGeohash = recipientLastGeohash
        )
        store.save(bundle)

        if (tryDirectDelivery(bundle)) {
            bundle = bundle.copy(status = "Delivered")
            store.save(bundle)
        }
        return bundle
    }

    suspend fun handle(packet: MeshPacket) {
        when (packet.type) {
            PacketType.DtnBundle -> handleBundle(packet)
            PacketType.DtnCustodyAck -> handleCustodyAck(packet)
            PacketType.DtnDeliveryReceipt -> handleDeliveryReceipt(packet)
            else -> {}
        }
    }

    suspend fun runDeliveryScan() {
        val active = store.getActive()
        if (active.isEmpty()) return
        val peers = sender.connectedPeers()
        val localGeohash = sender.localGeohash

        for (b in active) {
            var bundle = b
            if (bundle.status == "Delivered" || bundle.isExpired()) continue
            if (tryDirectDelivery(bundle)) {
                bundle = bundle.copy(status = "Delivered")
                store.save(bundle)
                continue
            }
            if (peers.isEmpty() || bundle.copyCount >= bundle.maxCopies) continue
            val targets = strategy.selectTargets(bundle, peers, localGeohash)
            for (target in targets) {
                if (bundle.copyCount >= bundle.maxCopies) break
                val pkt = bundlePacket(bundle)
                if (sender.send(pkt, target)) {
                    bundle = bundle.copy(copyCount = bundle.copyCount + 1)
                    store.save(bundle)
                    incentives.recordRelay(sender.localUhid, pkt)
                }
            }
        }
    }

    suspend fun expireStale(): Int = store.expireStale()
    suspend fun getActiveBundles(): List<DtnBundle> = store.getActive()

    private suspend fun tryDirectDelivery(bundle: DtnBundle): Boolean {
        val pkt = bundlePacket(bundle)
        for (peer in sender.connectedPeers()) {
            if (peer.uhid == bundle.recipientUhid) {
                if (sender.send(pkt, bundle.recipientUhid)) return true
                break
            }
        }
        return backend.syncDtnBundle(bundle)
    }

    private fun bundlePacket(bundle: DtnBundle): MeshPacket = MeshPacket(
        id = bundle.id,
        type = PacketType.DtnBundle,
        sourceUhid = sender.localUhid,
        destinationUhid = bundle.recipientUhid,
        ttl = 30,
        priority = bundle.priority.coerceIn(0, 255).toByte(),
        payload = encodeBundle(bundle)
    )

    private suspend fun handleBundle(packet: MeshPacket) {
        val bundle = decodeBundle(packet.payload) ?: return
        if (bundle.recipientUhid == sender.localUhid) {
            val delivered = bundle.copy(status = "Delivered")
            store.save(delivered)
            reputation?.recordDeliverySuccess(packet.sourceUhid, 0)
            onBundleReceived?.invoke(
                DtnBundleReceivedEvent(
                    bundleId = bundle.id,
                    senderUhid = bundle.senderUhid,
                    recipientUhid = bundle.recipientUhid,
                    encryptedPayload = bundle.encryptedPayload,
                    priority = runCatching { BundlePriority.fromValue(bundle.priority) }
                        .getOrDefault(BundlePriority.Normal),
                    hopCount = bundle.hopCount,
                    receivedAtUtc = Instant.now()
                )
            )
            sendDeliveryReceipt(delivered)
            return
        }
        if (store.getActiveCount() >= AetherNetConstants.DTN_MAX_BUNDLES_PER_NODE) {
            sendCustodyAck(bundle.id, packet.sourceUhid, accepted = false)
            return
        }
        val accepted = bundle.copy(status = "InCustody", hopCount = bundle.hopCount + 1)
        store.save(accepted)
        store.saveCustody(
            CustodyRecord(
                bundleId = bundle.id,
                fromUhid = packet.sourceUhid,
                toUhid = sender.localUhid,
                accepted = true
            )
        )
        sendCustodyAck(bundle.id, packet.sourceUhid, accepted = true)
        incentives.recordRelay(sender.localUhid, packet)
    }

    private suspend fun handleCustodyAck(packet: MeshPacket) {
        val (bundleId, accepted) = parseCustodyAck(packet.payload) ?: return
        if (!accepted) {
            reputation?.recordCustodyRefusal(packet.sourceUhid)
            return
        }
        val bundle = store.get(bundleId) ?: return
        store.save(bundle.copy(copyCount = bundle.copyCount + 1))
    }

    private suspend fun handleDeliveryReceipt(packet: MeshPacket) {
        val parsed = parseDeliveryReceipt(packet.payload) ?: return
        val bundle = store.get(parsed.bundleId)
        if (bundle != null) store.save(bundle.copy(status = "Delivered"))
        onBundleDelivered?.invoke(parsed)
    }

    private suspend fun sendCustodyAck(bundleId: UUID, toUhid: String, accepted: Boolean) {
        if (toUhid.isEmpty()) return
        val payload = encodeCustodyAck(bundleId, accepted)
        val pkt = MeshPacket(
            type = PacketType.DtnCustodyAck,
            sourceUhid = sender.localUhid,
            destinationUhid = toUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload
        )
        sender.send(pkt, toUhid)
    }

    private suspend fun sendDeliveryReceipt(bundle: DtnBundle) {
        if (bundle.senderUhid.isEmpty() || bundle.senderUhid == sender.localUhid) return
        val custody = store.getCustodyRecords(bundle.id)
        val payload = encodeDeliveryReceipt(
            DtnDeliveryReceipt(
                bundleId = bundle.id,
                recipientUhid = bundle.recipientUhid,
                totalHops = bundle.hopCount,
                totalCustodyTransfers = custody.size,
                deliveredAt = Instant.now()
            )
        )
        val pkt = MeshPacket(
            type = PacketType.DtnDeliveryReceipt,
            sourceUhid = sender.localUhid,
            destinationUhid = bundle.senderUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload
        )
        sender.send(pkt, bundle.senderUhid)
    }
}

// ─────────────── tiny JSON helpers (snake_case wire-stable) ───────────────

private fun encodeBundle(b: aethernet.models.DtnBundle): ByteArray {
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"id\":\"").append(b.id).append("\",")
    sb.append("\"sender_uhid\":\"").append(jsonEscape(b.senderUhid)).append("\",")
    sb.append("\"recipient_uhid\":\"").append(jsonEscape(b.recipientUhid)).append("\",")
    sb.append("\"encrypted_payload\":[")
    for ((i, x) in b.encryptedPayload.withIndex()) {
        if (i > 0) sb.append(',')
        sb.append((x.toInt() and 0xff))
    }
    sb.append("],")
    sb.append("\"priority\":").append(b.priority).append(',')
    sb.append("\"status_label\":\"").append(jsonEscape(b.status)).append("\",")
    sb.append("\"copy_count\":").append(b.copyCount).append(',')
    sb.append("\"max_copies\":").append(b.maxCopies).append(',')
    sb.append("\"sender_geohash\":")
    if (b.senderGeohash == null) sb.append("null")
    else sb.append('"').append(jsonEscape(b.senderGeohash)).append('"')
    sb.append(',')
    sb.append("\"recipient_last_geohash\":")
    if (b.recipientLastGeohash == null) sb.append("null")
    else sb.append('"').append(jsonEscape(b.recipientLastGeohash)).append('"')
    sb.append(',')
    sb.append("\"hop_count\":").append(b.hopCount).append(',')
    sb.append("\"created_at_ms\":").append(b.createdAt.toEpochMilli()).append(',')
    sb.append("\"expires_at_ms\":").append(b.expiresAt.toEpochMilli())
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}

private fun decodeBundle(payload: ByteArray): aethernet.models.DtnBundle? {
    return try {
        val s = String(payload, Charsets.UTF_8)
        val id = UUID.fromString(JsonReader.readString(s, "id") ?: return null)
        val senderUhid = JsonReader.readString(s, "sender_uhid") ?: return null
        val recipientUhid = JsonReader.readString(s, "recipient_uhid") ?: return null
        val encryptedPayload = JsonReader.readByteArray(s, "encrypted_payload")
        val priority = JsonReader.readInt(s, "priority") ?: 1
        val statusLabel = JsonReader.readString(s, "status_label") ?: "Pending"
        val copyCount = JsonReader.readInt(s, "copy_count") ?: 1
        val maxCopies = JsonReader.readInt(s, "max_copies") ?: aethernet.AetherNetConstants.DTN_MAX_COPIES
        val senderGeohash = JsonReader.readNullableString(s, "sender_geohash")
        val recipientLastGeohash = JsonReader.readNullableString(s, "recipient_last_geohash")
        val hopCount = JsonReader.readInt(s, "hop_count") ?: 0
        val createdAtMs = JsonReader.readLong(s, "created_at_ms") ?: 0L
        val expiresAtMs = JsonReader.readLong(s, "expires_at_ms") ?: 0L
        aethernet.models.DtnBundle(
            id = id,
            senderUhid = senderUhid,
            recipientUhid = recipientUhid,
            encryptedPayload = encryptedPayload,
            priority = priority,
            status = statusLabel,
            copyCount = copyCount,
            maxCopies = maxCopies,
            senderGeohash = senderGeohash,
            recipientLastGeohash = recipientLastGeohash,
            hopCount = hopCount,
            createdAt = Instant.ofEpochMilli(createdAtMs),
            expiresAt = Instant.ofEpochMilli(expiresAtMs)
        )
    } catch (_: Exception) {
        null
    }
}

private fun encodeCustodyAck(bundleId: UUID, accepted: Boolean): ByteArray {
    val s = "{\"bundle_id\":\"$bundleId\",\"accepted\":$accepted}"
    return s.toByteArray(Charsets.UTF_8)
}

private fun parseCustodyAck(payload: ByteArray): Pair<UUID, Boolean>? {
    return try {
        val s = String(payload, Charsets.UTF_8)
        val id = UUID.fromString(JsonReader.readString(s, "bundle_id") ?: return null)
        val accepted = JsonReader.readBool(s, "accepted") ?: return null
        Pair(id, accepted)
    } catch (_: Exception) {
        null
    }
}

private fun encodeDeliveryReceipt(r: aethernet.models.DtnDeliveryReceipt): ByteArray {
    val s = "{\"bundle_id\":\"${r.bundleId}\",\"recipient_uhid\":\"${jsonEscape(r.recipientUhid)}\"," +
        "\"total_hops\":${r.totalHops},\"total_custody_transfers\":${r.totalCustodyTransfers}," +
        "\"delivered_at_ms\":${r.deliveredAt.toEpochMilli()}}"
    return s.toByteArray(Charsets.UTF_8)
}

private fun parseDeliveryReceipt(payload: ByteArray): aethernet.models.DtnDeliveryReceipt? {
    return try {
        val s = String(payload, Charsets.UTF_8)
        val id = UUID.fromString(JsonReader.readString(s, "bundle_id") ?: return null)
        val recipient = JsonReader.readString(s, "recipient_uhid") ?: return null
        val totalHops = JsonReader.readInt(s, "total_hops") ?: 0
        val totalCustody = JsonReader.readInt(s, "total_custody_transfers") ?: 0
        val deliveredAtMs = JsonReader.readLong(s, "delivered_at_ms") ?: 0L
        aethernet.models.DtnDeliveryReceipt(
            bundleId = id,
            recipientUhid = recipient,
            totalHops = totalHops,
            totalCustodyTransfers = totalCustody,
            deliveredAt = Instant.ofEpochMilli(deliveredAtMs)
        )
    } catch (_: Exception) {
        null
    }
}

/**
 * Tiny single-pass JSON field reader. Tolerates the small subset of JSON our encoders emit.
 * Production hosts should replace with kotlinx.serialization or Jackson.
 */
private object JsonReader {
    fun readString(json: String, key: String): String? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        if (p >= json.length || json[p] != '"') return null
        p++
        val sb = StringBuilder()
        while (p < json.length) {
            val c = json[p]
            if (c == '\\' && p + 1 < json.length) {
                when (json[p + 1]) {
                    'n' -> sb.append('\n')
                    'r' -> sb.append('\r')
                    't' -> sb.append('\t')
                    '"' -> sb.append('"')
                    '\\' -> sb.append('\\')
                    else -> sb.append(json[p + 1])
                }
                p += 2
            } else if (c == '"') {
                return sb.toString()
            } else {
                sb.append(c); p++
            }
        }
        return null
    }

    fun readNullableString(json: String, key: String): String? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        if (p + 4 <= json.length && json.regionMatches(p, "null", 0, 4)) return null
        return readString(json, key)
    }

    fun readInt(json: String, key: String): Int? = readLong(json, key)?.toInt()

    fun readLong(json: String, key: String): Long? {
        val raw = readRawNumber(json, key) ?: return null
        return raw.toLongOrNull()
    }

    fun readBool(json: String, key: String): Boolean? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        if (p + 4 <= json.length && json.regionMatches(p, "true", 0, 4)) return true
        if (p + 5 <= json.length && json.regionMatches(p, "false", 0, 5)) return false
        return null
    }

    fun readByteArray(json: String, key: String): ByteArray {
        val needle = "\"$key\":["
        val i = json.indexOf(needle)
        if (i < 0) return ByteArray(0)
        val start = i + needle.length
        val end = json.indexOf(']', start)
        if (end < 0 || end <= start) return ByteArray(0)
        val body = json.substring(start, end).trim()
        if (body.isEmpty()) return ByteArray(0)
        val parts = body.split(',')
        val out = ByteArray(parts.size)
        for ((k, part) in parts.withIndex()) {
            out[k] = part.trim().toInt().toByte()
        }
        return out
    }

    private fun readRawNumber(json: String, key: String): String? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        val start = p
        while (p < json.length && (json[p].isDigit() || json[p] == '-' || json[p] == '+')) p++
        if (p == start) return null
        return json.substring(start, p)
    }
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
