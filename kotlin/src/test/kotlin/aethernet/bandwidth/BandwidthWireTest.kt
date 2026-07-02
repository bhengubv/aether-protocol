// SPDX-License-Identifier: MIT
package aethernet.bandwidth

import aethernet.FakeMeshSender
import aethernet.models.PeerInfo
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54), BandwidthGossip(55).
 * Binary little-endian byte-identity gates + send/handle behaviour. Direct mirror of the C#
 * `BandwidthWireTests`; the byte-identity gates assert against the SHARED canonical hex vectors in
 * `fixtures/bandwidth/vectors.json`.
 *
 * Uses the broadcast-capturing shared [FakeMeshSender] (whose `broadcast` returns the connected-peer
 * count, so the fan-out assertions add three peers to reproduce the C# `BroadcastAsync` return of 3).
 */
class BandwidthWireTest {

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xff) }

    private fun senderWith3Peers(localUhid: String = "aether:local:01"): FakeMeshSender {
        val s = FakeMeshSender(localUhid)
        s.addPeer(PeerInfo(uhid = "aether:peer:a", identityKey = ByteArray(0)))
        s.addPeer(PeerInfo(uhid = "aether:peer:b", identityKey = ByteArray(0)))
        s.addPeer(PeerInfo(uhid = "aether:peer:c", identityKey = ByteArray(0)))
        return s
    }

    // ── Byte-identity gates (fixtures/bandwidth/vectors.json) ──────────────────

    @Test fun probe_serializesToCanonicalBytes() {
        assertEquals(
            "2a00000000401e18240a0600",
            hex(BandwidthWireCodec.serializeProbe(BandwidthProbe(42u, 1_700_000_000_000_000L))),
        )
    }

    @Test fun ack_serializesToCanonicalBytes() {
        // senderReceiveUs (999) is local-only and must NOT change the wire bytes.
        val ack = BandwidthProbeAck(
            sequence = 42u,
            senderSendUs = 1_700_000_000_000_000L,
            receiverReceiveUs = 1_700_000_000_012_345L,
            receiverSendUs = 1_700_000_000_013_000L,
            senderReceiveUs = 999L,
            probeBytes = 1200,
        )
        assertEquals(
            "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000",
            hex(BandwidthWireCodec.serializeAck(ack)),
        )
    }

    @Test fun gossip_serializesToCanonicalBytes() {
        // peerUhid/transportName/measuredAt are not on the wire.
        val g = BandwidthGossipPayload(
            peerUhid = "peer",
            transportName = "tp",
            btlBwBps = 5_000_000L,
            rtPropUs = 25_000L,
            confidence = BandwidthConfidence.MEDIUM,
            measuredAt = java.time.Instant.EPOCH,
        )
        assertEquals("404b4c0000000000a861000002", hex(BandwidthWireCodec.serializeGossip(g)))
    }

    @Test fun ack_roundTrips_senderReceiveUsZeroed() {
        val back = BandwidthWireCodec.deserializeAck(
            BandwidthWireCodec.serializeAck(
                BandwidthProbeAck(
                    sequence = 7u,
                    senderSendUs = 100,
                    receiverReceiveUs = 200,
                    receiverSendUs = 300,
                    senderReceiveUs = 400,
                    probeBytes = 512,
                )
            )
        )
        assertEquals(7u, back.sequence)
        assertEquals(100L, back.senderSendUs)
        assertEquals(200L, back.receiverReceiveUs)
        assertEquals(300L, back.receiverSendUs)
        assertEquals(0L, back.senderReceiveUs) // not on wire
        assertEquals(512, back.probeBytes)
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    @Test fun sendProbe_emitsDirectedProbe() = runBlocking {
        val s = FakeMeshSender("aether:a:01")
        val svc = BandwidthWireService(s)
        assertTrue(svc.sendProbe("aether:b:02", BandwidthProbe(42u, 1_700_000_000_000_000L)))
        assertEquals(1, s.unicasts.size)
        val sent = s.unicasts[0]
        assertEquals(PacketType.BandwidthProbe, sent.packet.type)
        assertEquals("aether:b:02", sent.nextHopUhid)
    }

    @Test fun sendAck_emitsDirectedAck() = runBlocking {
        val s = FakeMeshSender("aether:local:01")
        val svc = BandwidthWireService(s)
        val ack = BandwidthProbeAck(
            sequence = 1u,
            senderSendUs = 2,
            receiverReceiveUs = 3,
            receiverSendUs = 4,
            senderReceiveUs = 5,
            probeBytes = 6,
        )
        assertTrue(svc.sendAck("aether:b:02", ack))
        assertEquals(1, s.unicasts.size)
        assertEquals(PacketType.BandwidthAck, s.unicasts[0].packet.type)
    }

    @Test fun broadcastGossip_emitsGossip_andHandleRaisesEvent_withSourcePeer() = runBlocking {
        val s = senderWith3Peers()
        val svc = BandwidthWireService(s)
        val g = BandwidthGossipPayload(
            peerUhid = "",
            transportName = "",
            btlBwBps = 5_000_000L,
            rtPropUs = 25_000L,
            confidence = BandwidthConfidence.MEDIUM,
            measuredAt = java.time.Instant.EPOCH,
        )
        assertEquals(3, svc.broadcastGossip(g))
        assertEquals(1, s.broadcasts.size)
        val sent = s.broadcasts[0]
        assertEquals(PacketType.BandwidthGossip, sent.type)

        var got: BandwidthGossipPayload? = null
        svc.onGossipReceived = { got = it }
        sent.sourceUhid = "aether:peer:09"
        assertTrue(svc.handle(sent))
        assertNotNull(got)
        assertEquals(5_000_000L, got!!.btlBwBps)
        assertEquals(25_000L, got!!.rtPropUs)
        assertEquals(BandwidthConfidence.MEDIUM, got!!.confidence)
        assertEquals("aether:peer:09", got!!.peerUhid)
    }

    @Test fun handle_probe_raisesProbeReceived_withSource() = runBlocking {
        val svc = BandwidthWireService(FakeMeshSender("aether:local:01"))
        var got: BandwidthProbeReceived? = null
        svc.onProbeReceived = { got = it }
        val pkt = MeshPacket(
            type = PacketType.BandwidthProbe,
            sourceUhid = "aether:x:01",
            payload = BandwidthWireCodec.serializeProbe(BandwidthProbe(9u, 123)),
        )
        assertTrue(svc.handle(pkt))
        assertNotNull(got)
        assertEquals(9u, got!!.probe.sequence)
        assertEquals("aether:x:01", got!!.fromUhid)
    }

    @Test fun handle_ack_raisesAckReceived() = runBlocking {
        val svc = BandwidthWireService(FakeMeshSender("aether:local:01"))
        var got: BandwidthProbeAck? = null
        svc.onAckReceived = { got = it }
        val pkt = MeshPacket(
            type = PacketType.BandwidthAck,
            sourceUhid = "aether:x:01",
            payload = BandwidthWireCodec.serializeAck(
                BandwidthProbeAck(
                    sequence = 3u,
                    senderSendUs = 10,
                    receiverReceiveUs = 20,
                    receiverSendUs = 30,
                    senderReceiveUs = 0,
                    probeBytes = 64,
                )
            ),
        )
        assertTrue(svc.handle(pkt))
        assertNotNull(got)
        assertEquals(3u, got!!.sequence)
        assertEquals(64, got!!.probeBytes)
    }

    @Test fun handle_wrongType_returnsFalse() = runBlocking {
        val svc = BandwidthWireService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(0))))
    }
}
