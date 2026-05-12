// SPDX-License-Identifier: MIT
package aether.dtn

import aether.AetherConstants
import aether.FakeMeshSender
import aether.models.BundlePriority
import aether.models.DtnBundle
import aether.models.NodeCapabilities
import aether.models.PeerInfo
import aether.protocol.MeshPacket
import aether.protocol.PacketType
import aether.security.NodeReputationService
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

private const val LOCAL = "local"

private data class DtnSvc(
    val svc: DtnService,
    val sender: FakeMeshSender,
    val store: InMemoryBundleStore,
)

private fun newSvc(): DtnSvc {
    val sender = FakeMeshSender(LOCAL)
    val store = InMemoryBundleStore()
    return DtnSvc(DtnService(sender, store), sender, store)
}

private fun buildBundlePacket(source: String, b: DtnBundle): MeshPacket {
    val payloadList = b.encryptedPayload.joinToString(",") { (it.toInt() and 0xff).toString() }
    val senderGh = b.senderGeohash?.let { "\"$it\"" } ?: "null"
    val recipGh = b.recipientLastGeohash?.let { "\"$it\"" } ?: "null"
    val json = "{" +
        "\"id\":\"${b.id}\"," +
        "\"sender_uhid\":\"${b.senderUhid}\"," +
        "\"recipient_uhid\":\"${b.recipientUhid}\"," +
        "\"encrypted_payload\":[$payloadList]," +
        "\"priority\":${b.priority}," +
        "\"status_label\":\"${b.status}\"," +
        "\"copy_count\":${b.copyCount}," +
        "\"max_copies\":${b.maxCopies}," +
        "\"sender_geohash\":$senderGh," +
        "\"recipient_last_geohash\":$recipGh," +
        "\"hop_count\":${b.hopCount}," +
        "\"created_at_ms\":${b.createdAt.toEpochMilli()}," +
        "\"expires_at_ms\":${b.expiresAt.toEpochMilli()}" +
        "}"
    return MeshPacket(
        type = PacketType.DtnBundle,
        sourceUhid = source,
        destinationUhid = b.recipientUhid,
        payload = json.toByteArray(Charsets.UTF_8),
    )
}

private fun nowMs() = System.currentTimeMillis()

class DtnServiceTest {

    // ─── CreateBundle ───────────────────────────────────────

    @Test fun createBundle_persistsAndAttemptsDelivery() = runBlocking {
        val (svc, _, store) = newSvc()
        val b = svc.createBundle("recipient", byteArrayOf(1, 2, 3), BundlePriority.Normal, null)
        assertEquals("recipient", b.recipientUhid)
        assertEquals("Pending", b.status)
        assertEquals(1, store.getActive().size)
    }

    @Test fun createBundle_withDirectPeer_deliversImmediately() = runBlocking {
        val (svc, sender, _) = newSvc()
        sender.addPeer(PeerInfo(
            uhid = "recipient",
            identityKey = ByteArray(0),
            capabilities = NodeCapabilities(dtnCarrier = true),
        ))
        val b = svc.createBundle("recipient", byteArrayOf(1, 2, 3), BundlePriority.Normal, null)
        assertEquals("Delivered", b.status)
        assertTrue(sender.unicasts.any {
            it.nextHopUhid == "recipient" && it.packet.type == PacketType.DtnBundle
        })
    }

    // ─── HandleAsync — DtnBundle ────────────────────────────

    @Test fun handle_asRecipient_marksDeliveredAndSendsReceipt() = runBlocking {
        val (svc, sender, store) = newSvc()
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = LOCAL,
            encryptedPayload = byteArrayOf(9),
        )
        val pkt = buildBundlePacket("alice", b)
        svc.handle(pkt)

        val stored = store.get(b.id)
        assertNotNull(stored)
        assertEquals("Delivered", stored!!.status)
        assertTrue(sender.unicasts.any {
            it.packet.type == PacketType.DtnDeliveryReceipt && it.nextHopUhid == "alice"
        })
    }

    @Test fun handle_notRecipientWithCapacity_acceptsCustody() = runBlocking {
        val (svc, sender, store) = newSvc()
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",
            encryptedPayload = byteArrayOf(1),
        )
        val pkt = buildBundlePacket("alice", b)
        svc.handle(pkt)

        val stored = store.get(b.id)
        assertNotNull(stored)
        assertEquals("InCustody", stored!!.status)
        assertEquals(1, stored.hopCount)
        assertTrue(sender.unicasts.any {
            it.packet.type == PacketType.DtnCustodyAck && it.nextHopUhid == "alice"
        })
    }

    @Test fun handle_atCapacity_refusesCustody() = runBlocking {
        val (svc, sender, store) = newSvc()
        repeat(AetherConstants.DTN_MAX_BUNDLES_PER_NODE) {
            val fill = DtnBundle(
                senderUhid = "x", recipientUhid = "y", encryptedPayload = ByteArray(0),
                status = "InCustody",
            )
            store.save(fill)
        }
        sender.clear()

        val b = DtnBundle(
            senderUhid = "alice", recipientUhid = "bob", encryptedPayload = ByteArray(0),
        )
        svc.handle(buildBundlePacket("alice", b))

        val ack = sender.unicasts.first { it.packet.type == PacketType.DtnCustodyAck }
        val body = String(ack.packet.payload, Charsets.UTF_8)
        assertTrue(body.contains("\"accepted\":false"))
    }

    // ─── DtnCustodyAck ───────────────────────────────────────

    @Test fun handle_positiveCustodyAck_incrementsCopyCount() = runBlocking {
        val (svc, _, store) = newSvc()
        val bundle = svc.createBundle("recipient", byteArrayOf(1), BundlePriority.Normal, null)
        val initial = bundle.copyCount

        val payload = "{\"bundle_id\":\"${bundle.id}\",\"accepted\":true}"
        val pkt = MeshPacket(
            type = PacketType.DtnCustodyAck,
            sourceUhid = "carrier",
            destinationUhid = LOCAL,
            payload = payload.toByteArray(Charsets.UTF_8),
        )
        svc.handle(pkt)

        val stored = store.get(bundle.id)
        assertEquals(initial + 1, stored!!.copyCount)
    }

    @Test fun handle_negativeCustodyAck_doesNotIncrement() = runBlocking {
        val (svc, _, store) = newSvc()
        val bundle = svc.createBundle("recipient", byteArrayOf(1), BundlePriority.Normal, null)
        val initial = bundle.copyCount

        val payload = "{\"bundle_id\":\"${bundle.id}\",\"accepted\":false}"
        val pkt = MeshPacket(
            type = PacketType.DtnCustodyAck,
            sourceUhid = "carrier",
            destinationUhid = LOCAL,
            payload = payload.toByteArray(Charsets.UTF_8),
        )
        svc.handle(pkt)

        val stored = store.get(bundle.id)
        assertEquals(initial, stored!!.copyCount)
    }

    // ─── DtnDeliveryReceipt ─────────────────────────────────

    @Test fun handle_deliveryReceipt_marksBundleDelivered() = runBlocking {
        val (svc, _, store) = newSvc()
        val bundle = svc.createBundle("recipient", byteArrayOf(1), BundlePriority.Normal, null)

        val payload = "{\"bundle_id\":\"${bundle.id}\",\"recipient_uhid\":\"recipient\"," +
            "\"total_hops\":3,\"total_custody_transfers\":2,\"delivered_at_ms\":0}"
        val pkt = MeshPacket(
            type = PacketType.DtnDeliveryReceipt,
            sourceUhid = "recipient",
            destinationUhid = LOCAL,
            payload = payload.toByteArray(Charsets.UTF_8),
        )
        svc.handle(pkt)

        val stored = store.get(bundle.id)
        assertEquals("Delivered", stored!!.status)
    }

    // ─── ExpireStale ────────────────────────────────────────

    @Test fun expireStale_flipsStatusForExpiredBundles() = runBlocking {
        val (svc, _, store) = newSvc()
        val expired = DtnBundle(
            senderUhid = "a", recipientUhid = "b", encryptedPayload = ByteArray(0),
            status = "Pending",
            expiresAt = Instant.now().minusSeconds(60),
        )
        store.save(expired)

        val fresh = DtnBundle(
            senderUhid = "a", recipientUhid = "b", encryptedPayload = ByteArray(0),
            status = "Pending",
        )
        store.save(fresh)

        val n = svc.expireStale()
        assertEquals(1, n)
        assertEquals("Pending", store.get(fresh.id)!!.status)
    }

    // ─── Reputation hooks ───────────────────────────────────

    @Test fun handle_deliveryToSelf_firesRecordDeliverySuccess() = runBlocking {
        val (svc, _, _) = newSvc()
        val rep = FakeReputation()
        svc.setReputation(rep)

        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = LOCAL,
            encryptedPayload = byteArrayOf(1),
        )
        svc.handle(buildBundlePacket("alice", b))

        assertEquals(1, rep.deliverySuccesses.size)
        assertEquals("alice", rep.deliverySuccesses[0])
    }

    @Test fun handle_bundleNotForUs_doesNotFireRecordDeliverySuccess() = runBlocking {
        val (svc, _, _) = newSvc()
        val rep = FakeReputation()
        svc.setReputation(rep)

        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",   // not LOCAL
            encryptedPayload = byteArrayOf(1),
        )
        svc.handle(buildBundlePacket("alice", b))

        assertTrue(rep.deliverySuccesses.isEmpty())
    }

    @Test fun handle_negativeCustodyAck_firesRecordCustodyRefusal() = runBlocking {
        val (svc, _, _) = newSvc()
        val rep = FakeReputation()
        svc.setReputation(rep)

        val payload = "{\"bundle_id\":\"${UUID.randomUUID()}\",\"accepted\":false}"
        val pkt = MeshPacket(
            type = PacketType.DtnCustodyAck,
            sourceUhid = "carrier",
            destinationUhid = LOCAL,
            payload = payload.toByteArray(Charsets.UTF_8),
        )
        svc.handle(pkt)

        assertEquals(1, rep.custodyRefusals.size)
        assertEquals("carrier", rep.custodyRefusals[0])
    }
}

/** Test double for [NodeReputationService] that records calls without side-effects. */
private class FakeReputation : NodeReputationService() {
    val deliverySuccesses = mutableListOf<String>()
    val custodyRefusals   = mutableListOf<String>()

    override fun recordDeliverySuccess(uhid: String, roundTripMs: Int) {
        deliverySuccesses += uhid
    }

    override fun recordCustodyRefusal(uhid: String) {
        custodyRefusals += uhid
    }
}
