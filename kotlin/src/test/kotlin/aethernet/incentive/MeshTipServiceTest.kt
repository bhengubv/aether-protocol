// SPDX-License-Identifier: MIT

package aethernet.incentive

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Behavioural tests for [MeshTipService] — the send path (build/sign/route a
 * TipPacket) and the receive path (validate, hand to the settlement provider,
 * relay onward). The fixture test only checks the payload bytes; this exercises
 * the service logic.
 */
class MeshTipServiceTest {

    private class FakeSender(override val localUhid: String) : MeshTipService.MeshSender {
        val unicasts = mutableListOf<Pair<MeshPacket, String>>()
        val broadcasts = mutableListOf<MeshPacket>()

        override suspend fun send(packet: MeshPacket, nextHopUhid: String): Boolean {
            unicasts.add(packet to nextHopUhid)
            return true
        }

        override suspend fun broadcast(packet: MeshPacket): Int {
            broadcasts.add(packet)
            return 1
        }
    }

    private class NoopEnvelopeSigner : MeshTipService.PacketSigner {
        override fun sign(packet: MeshPacket) { /* envelope signing is irrelevant here */ }
    }

    /** Produces a well-formed 64-byte (Ed25519-length) signature so the inbound check passes. */
    private class FixedIdentitySigner : MeshTipService.IdentitySigner {
        override fun signData(data: ByteArray): ByteArray = ByteArray(64) { 0x11 }
    }

    private class FixedRoute(private val hop: String?) : MeshTipService.RouteResolver {
        override fun findNextHop(destinationUhid: String): String? = hop
    }

    private fun service(
        sender: FakeSender,
        route: String?,
        settle: MeshTipService.MeshTipSettlementProvider? = null,
    ) = MeshTipService(sender, NoopEnvelopeSigner(), FixedIdentitySigner(), FixedRoute(route), settle)

    @Test
    fun `sendTip with no route broadcasts a TipPacket addressed to the recipient`() = runBlocking {
        val sender = FakeSender("alice")
        val svc = service(sender, route = null)

        val pkt = svc.sendTip("bob", "10.50", "relay", null, 1_700_000_000_000L)

        assertEquals(PacketType.TipPacket, pkt.type)
        assertEquals("bob", pkt.destinationUhid)
        assertEquals(1, sender.broadcasts.size, "no route should broadcast")
        assertEquals(0, sender.unicasts.size)
    }

    @Test
    fun `sendTip with a route unicasts to the next hop`() = runBlocking {
        val sender = FakeSender("alice")
        val svc = service(sender, route = "relay-1")

        svc.sendTip("bob", "1.00", "relay", null, 1_700_000_000_000L)

        assertEquals(1, sender.unicasts.size, "a known route should unicast")
        assertEquals("relay-1", sender.unicasts[0].second)
        assertEquals(0, sender.broadcasts.size)
    }

    @Test
    fun `handleTipPacket on a well-formed tip invokes settlement and returns true`() = runBlocking {
        val tx = service(FakeSender("alice"), route = null)
        val tip = tx.sendTip("bob", "5.00", "relay", null, 1_700_000_000_000L)

        var settled: TipPacketPayload? = null
        val rxSettle = object : MeshTipService.MeshTipSettlementProvider {
            override suspend fun settleMeshTip(payload: TipPacketPayload) {
                settled = payload
            }
        }
        val rxSender = FakeSender("bob")
        val rx = service(rxSender, route = null, settle = rxSettle)

        val accepted = rx.handleTipPacket(tip)

        assertTrue(accepted, "a well-formed tip must be accepted")
        assertNotNull(settled, "the settlement provider must be invoked")
        assertEquals("bob", settled!!.recipientUhid)
        // bob is the destination, so it does NOT relay onward.
        assertEquals(0, rxSender.unicasts.size + rxSender.broadcasts.size, "destination must not relay")
    }

    @Test
    fun `handleTipPacket relays onward when this node is not the destination`() = runBlocking {
        val tx = service(FakeSender("alice"), route = null)
        val tip = tx.sendTip("carol", "2.00", "relay", null, 1_700_000_000_000L)

        val rxSender = FakeSender("bob") // an intermediate relay
        val rx = service(rxSender, route = "hop-to-carol")

        val accepted = rx.handleTipPacket(tip)

        assertTrue(accepted)
        assertEquals(1, rxSender.unicasts.size, "an intermediate node must relay the tip onward")
        assertEquals("hop-to-carol", rxSender.unicasts[0].second)
    }

    @Test
    fun `handleTipPacket drops a non-tip packet`() = runBlocking {
        val rx = service(FakeSender("bob"), route = null)
        val wrong = MeshPacket(type = PacketType.Data, sourceUhid = "alice", destinationUhid = "bob")
        assertFalse(rx.handleTipPacket(wrong), "a non-TipPacket must be ignored")
    }

    @Test
    fun `handleTipPacket drops a tip payload missing required fields`() = runBlocking {
        val rx = service(FakeSender("bob"), route = null)
        val empty = MeshPacket(
            type = PacketType.TipPacket,
            sourceUhid = "alice",
            destinationUhid = "bob",
            payload = "{}".toByteArray(Charsets.UTF_8),
        )
        assertFalse(rx.handleTipPacket(empty), "a payload with no tipper/recipient must be dropped")
    }
}
