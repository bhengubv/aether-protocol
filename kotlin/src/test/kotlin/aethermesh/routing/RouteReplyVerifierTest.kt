// SPDX-License-Identifier: MIT
package aethermesh.routing

import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

// ── helpers ───────────────────────────────────────────────────────────────────

private fun makePacket(type: PacketType = PacketType.RouteReply) =
    MeshPacket(type = type, sourceUhid = "node-2", destinationUhid = "node-1")

// ── AcceptAllRouteReplyVerifier ───────────────────────────────────────────────

class RouteReplyVerifierTest {

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

    @Test
    fun `default interface verify returns true`() = runBlocking {
        // Use an anonymous implementation that relies on the interface default.
        val verifier = object : RouteReplyVerifier {}
        assertTrue(verifier.verify(makePacket()))
    }

    @Test
    fun `custom reject-all verifier can override the default`() = runBlocking {
        val verifier = object : RouteReplyVerifier {
            override suspend fun verify(routeReply: MeshPacket): Boolean = false
        }
        assertFalse(verifier.verify(makePacket()))
    }
}
