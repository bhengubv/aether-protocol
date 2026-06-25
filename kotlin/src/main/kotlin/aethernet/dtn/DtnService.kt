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
 *
 * Bundles, custody-acks and delivery-receipts ride the canonical binary wire
 * format ([DtnEnvelope]) so they interoperate byte-for-byte with the other
 * seven AetherNet SDKs.
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
        payload = DtnEnvelope.serializeBundle(bundle)
    )

    private suspend fun handleBundle(packet: MeshPacket) {
        val bundle = try {
            DtnEnvelope.deserializeBundle(packet.payload)
        } catch (_: Exception) {
            return
        }
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
        val (bundleId, accepted) = try {
            DtnEnvelope.deserializeCustodyAck(packet.payload)
        } catch (_: Exception) {
            return
        }
        if (!accepted) {
            reputation?.recordCustodyRefusal(packet.sourceUhid)
            return
        }
        val bundle = store.get(bundleId) ?: return
        store.save(bundle.copy(copyCount = bundle.copyCount + 1))
    }

    private suspend fun handleDeliveryReceipt(packet: MeshPacket) {
        val parsed = try {
            DtnEnvelope.deserializeDeliveryReceipt(packet.payload)
        } catch (_: Exception) {
            return
        }
        val bundle = store.get(parsed.bundleId)
        if (bundle != null) store.save(bundle.copy(status = "Delivered"))
        onBundleDelivered?.invoke(parsed)
    }

    private suspend fun sendCustodyAck(bundleId: UUID, toUhid: String, accepted: Boolean) {
        if (toUhid.isEmpty()) return
        val payload = DtnEnvelope.serializeCustodyAck(bundleId, accepted)
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
        val payload = DtnEnvelope.serializeDeliveryReceipt(
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
