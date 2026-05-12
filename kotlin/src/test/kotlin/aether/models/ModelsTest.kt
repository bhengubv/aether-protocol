// SPDX-License-Identifier: MIT

package aether.models

import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.*

// ── NodeCapabilities.toBitfield / fromBitfield ────────────────────────────────

class NodeCapabilitiesTest {

    @Test fun `default capabilities all false, bitfield is 0`() {
        assertEquals(0, NodeCapabilities().toBitfield())
    }

    @Test fun `ble flag sets bit 1`() {
        assertEquals(1, NodeCapabilities(ble = true).toBitfield())
    }

    @Test fun `wifiDirect flag sets bit 2`() {
        assertEquals(2, NodeCapabilities(wifiDirect = true).toBitfield())
    }

    @Test fun `gateway flag sets bit 4`() {
        assertEquals(4, NodeCapabilities(gateway = true).toBitfield())
    }

    @Test fun `relay flag sets bit 8`() {
        assertEquals(8, NodeCapabilities(relay = true).toBitfield())
    }

    @Test fun `sos flag sets bit 16`() {
        assertEquals(16, NodeCapabilities(sos = true).toBitfield())
    }

    @Test fun `streaming flag sets bit 32`() {
        assertEquals(32, NodeCapabilities(streaming = true).toBitfield())
    }

    @Test fun `voice flag sets bit 64`() {
        assertEquals(64, NodeCapabilities(voice = true).toBitfield())
    }

    @Test fun `dtnCarrier flag sets bit 128`() {
        assertEquals(128, NodeCapabilities(dtnCarrier = true).toBitfield())
    }

    @Test fun `nearLink flag sets bit 256`() {
        assertEquals(256, NodeCapabilities(nearLink = true).toBitfield())
    }

    @Test fun `video flag sets bit 512`() {
        assertEquals(512, NodeCapabilities(video = true).toBitfield())
    }

    @Test fun `all flags set produces 1023`() {
        val all = NodeCapabilities(
            ble = true, wifiDirect = true, gateway = true, relay = true,
            sos = true, streaming = true, voice = true, dtnCarrier = true,
            nearLink = true, video = true
        )
        assertEquals(1023, all.toBitfield())
    }

    @Test fun `fromBitfield 0 produces all-false capabilities`() {
        val caps = NodeCapabilities.fromBitfield(0)
        assertFalse(caps.ble)
        assertFalse(caps.wifiDirect)
        assertFalse(caps.relay)
        assertFalse(caps.video)
    }

    @Test fun `fromBitfield 1023 produces all-true capabilities`() {
        val caps = NodeCapabilities.fromBitfield(1023)
        assertTrue(caps.ble)
        assertTrue(caps.wifiDirect)
        assertTrue(caps.gateway)
        assertTrue(caps.relay)
        assertTrue(caps.sos)
        assertTrue(caps.streaming)
        assertTrue(caps.voice)
        assertTrue(caps.dtnCarrier)
        assertTrue(caps.nearLink)
        assertTrue(caps.video)
    }

    @Test fun `toBitfield and fromBitfield are inverse`() {
        val original = NodeCapabilities(ble = true, relay = true, voice = true)
        val bitfield = original.toBitfield()
        val restored = NodeCapabilities.fromBitfield(bitfield)
        assertEquals(original, restored)
    }

    @Test fun `fromBitfield round-trips for single flags`() {
        for (bit in 0..9) {
            val bits = 1 shl bit
            val caps = NodeCapabilities.fromBitfield(bits)
            assertEquals(bits, caps.toBitfield(), "round-trip failed for bit $bit (value $bits)")
        }
    }
}

// ── RouteEntry.isExpired ──────────────────────────────────────────────────────

class RouteEntryTest {

    @Test fun `isExpired returns true when expiry is in the past`() {
        val entry = RouteEntry(
            destinationUhid = "dest",
            nextHopUhid = "hop",
            hopCount = 2,
            expiresAt = Instant.now().minusSeconds(10)
        )
        assertTrue(entry.isExpired())
    }

    @Test fun `isExpired returns false when expiry is in the future`() {
        val entry = RouteEntry(
            destinationUhid = "dest",
            nextHopUhid = "hop",
            hopCount = 1,
            expiresAt = Instant.now().plusSeconds(60)
        )
        assertFalse(entry.isExpired())
    }

    @Test fun `isExpired false for far-future expiry`() {
        val entry = RouteEntry(
            destinationUhid = "dest",
            nextHopUhid = "hop",
            hopCount = 1,
            expiresAt = Instant.now().plusSeconds(86400)
        )
        assertFalse(entry.isExpired())
    }

    @Test fun `default expiry is in the future`() {
        val entry = RouteEntry(destinationUhid = "d", nextHopUhid = "h", hopCount = 1)
        assertFalse(entry.isExpired(), "default route should not be immediately expired")
    }
}

// ── DtnBundle.isExpired ───────────────────────────────────────────────────────

class DtnBundleTest {

    @Test fun `isExpired returns true for past expiry`() {
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",
            encryptedPayload = byteArrayOf(1, 2, 3),
            expiresAt = Instant.now().minusSeconds(300)
        )
        assertTrue(b.isExpired())
    }

    @Test fun `isExpired returns false for future expiry`() {
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",
            encryptedPayload = byteArrayOf(1, 2, 3),
            expiresAt = Instant.now().plusSeconds(3600)
        )
        assertFalse(b.isExpired())
    }

    @Test fun `default expiry is in the future`() {
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",
            encryptedPayload = byteArrayOf(1)
        )
        assertFalse(b.isExpired())
    }

    @Test fun `id is auto-generated UUID when not provided`() {
        val b = DtnBundle(
            senderUhid = "alice",
            recipientUhid = "bob",
            encryptedPayload = byteArrayOf(1)
        )
        assertNotNull(b.id)
    }

    @Test fun `two bundles with different auto-ids are not equal`() {
        val b1 = DtnBundle(senderUhid = "a", recipientUhid = "b", encryptedPayload = byteArrayOf(1))
        val b2 = DtnBundle(senderUhid = "a", recipientUhid = "b", encryptedPayload = byteArrayOf(1))
        assertNotEquals(b1.id, b2.id)
    }
}

// ── BundleStatus enum ─────────────────────────────────────────────────────────

class BundleStatusTest {

    @Test fun `Pending has value 0`() {
        assertEquals(0, BundleStatus.Pending.value)
    }

    @Test fun `InCustody has value 1`() {
        assertEquals(1, BundleStatus.InCustody.value)
    }

    @Test fun `Delivered has value 2`() {
        assertEquals(2, BundleStatus.Delivered.value)
    }

    @Test fun `Expired has value 3`() {
        assertEquals(3, BundleStatus.Expired.value)
    }

    @Test fun `Failed has value 4`() {
        assertEquals(4, BundleStatus.Failed.value)
    }

    @Test fun `fromValue round-trips all statuses`() {
        for (status in BundleStatus.values()) {
            assertEquals(status, BundleStatus.fromValue(status.value))
        }
    }
}

// ── BundlePriority enum ───────────────────────────────────────────────────────

class BundlePriorityTest {

    @Test fun `Low has value 0`() {
        assertEquals(0, BundlePriority.Low.value)
    }

    @Test fun `Normal has value 1`() {
        assertEquals(1, BundlePriority.Normal.value)
    }

    @Test fun `High has value 2`() {
        assertEquals(2, BundlePriority.High.value)
    }

    @Test fun `Sos has value 3`() {
        assertEquals(3, BundlePriority.Sos.value)
    }

    @Test fun `fromValue round-trips all priorities`() {
        for (priority in BundlePriority.values()) {
            assertEquals(priority, BundlePriority.fromValue(priority.value))
        }
    }

    @Test fun `priorities are in increasing order`() {
        assertTrue(BundlePriority.Low.value < BundlePriority.Normal.value)
        assertTrue(BundlePriority.Normal.value < BundlePriority.High.value)
        assertTrue(BundlePriority.High.value < BundlePriority.Sos.value)
    }
}

// ── CustodyRecord ─────────────────────────────────────────────────────────────

class CustodyRecordTest {

    @Test fun `creates with all required fields`() {
        val rec = CustodyRecord(
            bundleId = UUID.randomUUID(),
            fromUhid = "alice",
            toUhid = "bob",
            accepted = true
        )
        assertEquals("alice", rec.fromUhid)
        assertEquals("bob", rec.toUhid)
        assertTrue(rec.accepted)
    }

    @Test fun `auto-generates id when not provided`() {
        val rec = CustodyRecord(
            bundleId = UUID.randomUUID(),
            fromUhid = "a",
            toUhid = "b",
            accepted = false
        )
        assertNotNull(rec.id)
    }

    @Test fun `rejected custody record has accepted=false`() {
        val rec = CustodyRecord(
            bundleId = UUID.randomUUID(),
            fromUhid = "x",
            toUhid = "y",
            accepted = false
        )
        assertFalse(rec.accepted)
    }
}

// ── DtnDeliveryReceipt ────────────────────────────────────────────────────────

class DtnDeliveryReceiptTest {

    @Test fun `creates with all required fields`() {
        val receipt = DtnDeliveryReceipt(
            bundleId = UUID.randomUUID(),
            recipientUhid = "bob",
            totalHops = 4,
            totalCustodyTransfers = 2
        )
        assertEquals("bob", receipt.recipientUhid)
        assertEquals(4, receipt.totalHops)
        assertEquals(2, receipt.totalCustodyTransfers)
    }

    @Test fun `deliveredAt defaults to now`() {
        val before = Instant.now().minusSeconds(1)
        val receipt = DtnDeliveryReceipt(
            bundleId = UUID.randomUUID(),
            recipientUhid = "bob",
            totalHops = 1,
            totalCustodyTransfers = 0
        )
        assertTrue(receipt.deliveredAt >= before)
    }
}

// ── SosAlert ──────────────────────────────────────────────────────────────────

class SosAlertTest {

    @Test fun `creates with senderUhid only`() {
        val alert = SosAlert(senderUhid = "alice")
        assertEquals("alice", alert.senderUhid)
        assertEquals("sos", alert.broadcastType)
        assertNull(alert.message)
    }

    @Test fun `latitude and longitude default to 0`() {
        val alert = SosAlert(senderUhid = "bob")
        assertEquals(0.0, alert.latitude)
        assertEquals(0.0, alert.longitude)
    }

    @Test fun `auto-generates id`() {
        val a1 = SosAlert(senderUhid = "alice")
        val a2 = SosAlert(senderUhid = "alice")
        assertNotEquals(a1.id, a2.id)
    }

    @Test fun `can set message and coordinates`() {
        val alert = SosAlert(
            senderUhid = "alice",
            message = "Need help!",
            latitude = -26.2041,
            longitude = 28.0473,
            broadcastType = "panic"
        )
        assertEquals("Need help!", alert.message)
        assertEquals(-26.2041, alert.latitude)
        assertEquals(28.0473, alert.longitude)
        assertEquals("panic", alert.broadcastType)
    }
}

// ── AetherNode ────────────────────────────────────────────────────────────────

class AetherNodeTest {

    @Test fun `creates with required fields`() {
        val node = AetherNode(
            uhid = "node-001",
            identityPublicKey = ByteArray(32) { it.toByte() }
        )
        assertEquals("node-001", node.uhid)
        assertEquals(32, node.identityPublicKey.size)
    }

    @Test fun `default capabilities are all false`() {
        val node = AetherNode(
            uhid = "n",
            identityPublicKey = ByteArray(32)
        )
        assertEquals(NodeCapabilities(), node.capabilities)
    }

    @Test fun `geohash defaults to null`() {
        val node = AetherNode(uhid = "n", identityPublicKey = ByteArray(32))
        assertNull(node.geohash)
    }

    @Test fun `equals uses content-equality for identityPublicKey`() {
        val key = ByteArray(32) { it.toByte() }
        val n1 = AetherNode("n", key.copyOf())
        val n2 = AetherNode("n", key.copyOf())
        assertEquals(n1, n2)
    }
}

// ── PeerInfo ──────────────────────────────────────────────────────────────────

class PeerInfoTest {

    @Test fun `creates with required fields`() {
        val peer = PeerInfo(
            uhid = "peer-1",
            identityKey = ByteArray(32)
        )
        assertEquals("peer-1", peer.uhid)
        assertEquals(50, peer.reliabilityScore) // default
    }

    @Test fun `geohash defaults to null`() {
        val peer = PeerInfo(uhid = "p", identityKey = ByteArray(32))
        assertNull(peer.geohash)
    }

    @Test fun `equals uses content-equality for identityKey`() {
        val key = ByteArray(32) { 0x42 }
        val p1 = PeerInfo("peer", key.copyOf())
        val p2 = PeerInfo("peer", key.copyOf())
        assertEquals(p1, p2)
    }
}
