// SPDX-License-Identifier: MIT
package aethernet.routing

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

// ── helpers ───────────────────────────────────────────────────────────────────

private fun makePacket(type: PacketType = PacketType.RouteReply) =
    MeshPacket(type = type, sourceUhid = "node-2", destinationUhid = "node-1")

// ── RouteReplyVerifier default + built-in verifiers ───────────────────────────

class RouteReplyVerifierTest {

    // ── AcceptAllRouteReplyVerifier (explicit INSECURE opt-in) ────────────────

    @Test
    fun `AcceptAll verify returns true for RouteReply`() = runBlocking {
        val verifier = AcceptAllRouteReplyVerifier()
        assertTrue(verifier.verify(makePacket(PacketType.RouteReply)))
    }

    @Test
    fun `AcceptAll verify returns true for any packet type`() = runBlocking {
        val verifier = AcceptAllRouteReplyVerifier()
        // The AcceptAll implementation does not gate on packet type.
        assertTrue(verifier.verify(makePacket(PacketType.RouteRequest)))
        assertTrue(verifier.verify(makePacket(PacketType.Data)))
    }

    @Test
    fun `AcceptAll verify returns true for empty-source packet`() = runBlocking {
        val verifier = AcceptAllRouteReplyVerifier()
        val pkt = MeshPacket(type = PacketType.RouteReply, sourceUhid = "", destinationUhid = "node-1")
        assertTrue(verifier.verify(pkt))
    }

    // ── Fail-closed default + RejectAllRouteReplyVerifier ─────────────────────

    @Test
    fun `default interface verify returns false (fail-closed)`() = runBlocking {
        // An anonymous implementation that relies on the interface default must REJECT — an
        // unconfigured / half-built verifier can never be exploited to trust an unverified RREP.
        val verifier = object : RouteReplyVerifier {}
        assertFalse(verifier.verify(makePacket()))
    }

    @Test
    fun `RejectAll verify returns false for RouteReply`() = runBlocking {
        val verifier = RejectAllRouteReplyVerifier()
        assertFalse(verifier.verify(makePacket(PacketType.RouteReply)))
    }

    @Test
    fun `custom accept-all verifier can override the fail-closed default`() = runBlocking {
        val verifier = object : RouteReplyVerifier {
            override suspend fun verify(routeReply: MeshPacket): Boolean = true
        }
        assertTrue(verifier.verify(makePacket()))
    }
}
