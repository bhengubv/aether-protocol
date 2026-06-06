// SPDX-License-Identifier: MIT
package aethermesh.handshake

import aethermesh.FakeMeshSender
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Tests for [HandshakeService] — the Hello/HelloAck capability handshake.
 *
 * Mirrors `AetherMesh.Core.Tests.HandshakeServiceTests` (C#) for protocol-level
 * parity. Cross-language wire-shape compatibility is verified separately
 * (a C# peer can deserialise a Kotlin-emitted Hello packet because both
 * encode the same snake_case JSON shape).
 */
class HandshakeServiceTest {

    // ─── Hello / HelloAck exchange ─────────────────────────────────────────

    @Test
    fun initiate_sendsHelloPacket() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        svc.initiate("bob")

        assertEquals(1, sender.unicasts.size)
        val packet = sender.unicasts[0].packet
        assertEquals(PacketType.Hello, packet.type)
        assertEquals("alice", packet.sourceUhid)
        assertEquals("bob", packet.destinationUhid)
        assertEquals(1, packet.ttl) // direct hop only
        assertEquals("bob", sender.unicasts[0].nextHopUhid)
    }

    @Test
    fun initiate_isIdempotent() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        svc.initiate("bob")
        svc.initiate("bob")
        svc.initiate("bob")

        assertEquals(1, sender.unicasts.size, "duplicate Hellos must be suppressed")
    }

    @Test
    fun initiate_skipsSelf() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        svc.initiate("alice")

        assertEquals(0, sender.unicasts.size)
    }

    @Test
    fun handleHello_locksInCapsAndRepliesWithAck() = runBlocking {
        val sender = FakeMeshSender("bob")
        val svc = HandshakeService(sender)

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        val helloFromAlice = newHelloPacket(
            from = "alice",
            to = "bob",
            payload = HelloPayload(
                minVersion = 1,
                maxVersion = 2,
                capabilities = listOf("signal-x3dh", "double-ratchet", "voice"),
                implementation = "aether-csharp/1.0.0",
            ),
        )

        svc.handleHello(helloFromAlice)

        // Negotiated record was created.
        assertNotNull(negotiated)
        assertEquals("alice", negotiated!!.peerUhid)
        assertEquals(2.toByte(), negotiated!!.negotiatedVersion)
        // Intersection of Alice's caps and Bob's defaults.
        assertTrue("signal-x3dh" in negotiated!!.capabilities)
        assertTrue("double-ratchet" in negotiated!!.capabilities)
        assertTrue("voice" in negotiated!!.capabilities)
        assertEquals("aether-csharp/1.0.0", negotiated!!.implementationVersion)

        // HelloAck reply sent.
        assertEquals(1, sender.unicasts.size)
        val ack = sender.unicasts[0].packet
        assertEquals(PacketType.HelloAck, ack.type)
        assertEquals("bob", ack.sourceUhid)
        assertEquals("alice", ack.destinationUhid)
    }

    @Test
    fun handleHello_malformedPayloadIgnoredSilently() = runBlocking {
        val sender = FakeMeshSender("bob")
        val svc = HandshakeService(sender)

        var fired = false
        svc.onPeerNegotiated = { fired = true }

        val malformed = MeshPacket(
            type = PacketType.Hello,
            sourceUhid = "alice",
            destinationUhid = "bob",
            payload = "not json".toByteArray(),
        )
        svc.handleHello(malformed)

        assertFalse(fired)
        assertEquals(0, sender.unicasts.size)
    }

    @Test
    fun handleHelloAck_locksInWithoutReplying() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        // Alice initiates first.
        svc.initiate("bob")
        sender.clear()

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        val ackFromBob = newAckPacket(
            from = "bob",
            to = "alice",
            payload = HelloPayload(
                minVersion = 1, maxVersion = 2,
                capabilities = listOf("signal-x3dh", "double-ratchet"),
                implementation = "aether-csharp/1.0.0",
            ),
        )
        svc.handleHelloAck(ackFromBob)

        assertNotNull(negotiated)
        assertEquals(2.toByte(), negotiated!!.negotiatedVersion)
        // Receiving an ack does NOT generate further packets.
        assertEquals(0, sender.unicasts.size)
    }

    // ─── Version selection ─────────────────────────────────────────────────

    @Test
    fun negotiation_picksLowestMutualMax() = runBlocking {
        val sender = FakeMeshSender("bob")
        // We support up to version 2, peer supports up to version 5 — pick 2.
        val svc = HandshakeService(sender, ourMinVersion = 1, ourMaxVersion = 2)

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(1, 5, listOf("signal-x3dh"), "exotic-impl"),
            ),
        )

        assertNotNull(negotiated)
        assertEquals(2.toByte(), negotiated!!.negotiatedVersion)
    }

    @Test
    fun negotiation_picksLowestMutualMaxFromOur() = runBlocking {
        val sender = FakeMeshSender("bob")
        // We support up to version 7, peer supports up to version 2 — pick 2.
        val svc = HandshakeService(sender, ourMinVersion = 1, ourMaxVersion = 7)

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(1, 2, listOf("signal-x3dh"), ""),
            ),
        )

        assertNotNull(negotiated)
        assertEquals(2.toByte(), negotiated!!.negotiatedVersion)
    }

    @Test
    fun negotiation_noVersionOverlapFiresIncompatibleAndDoesNotReply() = runBlocking {
        val sender = FakeMeshSender("bob")
        // We speak only v3..v5; peer speaks only v1..v2 — no overlap.
        val svc = HandshakeService(sender, ourMinVersion = 3, ourMaxVersion = 5)

        var incompatible: IncompatiblePeerEvent? = null
        var negotiated: PeerCapabilities? = null
        svc.onIncompatiblePeer = { incompatible = it }
        svc.onPeerNegotiated = { negotiated = it }

        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(1, 2, listOf("signal-x3dh"), ""),
            ),
        )

        assertNotNull(incompatible)
        assertEquals("alice", incompatible!!.peerUhid)
        assertNull(negotiated)
        assertNull(svc.getPeerCapabilities("alice"))
        assertEquals(0, sender.unicasts.size)
    }

    @Test
    fun negotiation_invertedRangeFiresIncompatible() = runBlocking {
        val sender = FakeMeshSender("bob")
        val svc = HandshakeService(sender)

        var incompatible: IncompatiblePeerEvent? = null
        svc.onIncompatiblePeer = { incompatible = it }

        // Inverted range: min > max.
        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(5, 1, listOf("signal-x3dh"), ""),
            ),
        )

        assertNotNull(incompatible)
        assertTrue(incompatible!!.reason.contains("inverted"))
        assertEquals(0, sender.unicasts.size)
    }

    // ─── Capability intersection ───────────────────────────────────────────

    @Test
    fun negotiation_intersectsCapabilities() = runBlocking {
        val sender = FakeMeshSender("bob")
        val svc = HandshakeService(
            sender,
            ourCapabilities = setOf("signal-x3dh", "double-ratchet", "voice"),
        )

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        // Peer claims a different mix — overlap is {signal-x3dh, voice}.
        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(
                    1, 2,
                    listOf("signal-x3dh", "video", "voice", "encrypted-storage"),
                    "exotic-impl",
                ),
            ),
        )

        assertNotNull(negotiated)
        val caps = negotiated!!.capabilities
        assertEquals(setOf("signal-x3dh", "voice"), caps)
    }

    @Test
    fun negotiation_emptyCapabilityIntersection() = runBlocking {
        val sender = FakeMeshSender("bob")
        val svc = HandshakeService(
            sender,
            ourCapabilities = setOf("voice", "stream"),
        )

        var negotiated: PeerCapabilities? = null
        svc.onPeerNegotiated = { negotiated = it }

        // Peer's caps are entirely disjoint from ours.
        svc.handleHello(
            newHelloPacket(
                from = "alice", to = "bob",
                payload = HelloPayload(1, 2, listOf("signal-x3dh", "double-ratchet"), ""),
            ),
        )

        assertNotNull(negotiated)
        assertTrue(negotiated!!.capabilities.isEmpty())
        // But version overlap exists, so handshake still succeeds.
        assertEquals(2.toByte(), negotiated!!.negotiatedVersion)
    }

    // ─── Duplicate suppression ─────────────────────────────────────────────

    @Test
    fun helloSent_setSuppressesDuplicateInitiates() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        svc.initiate("bob")
        svc.initiate("bob")
        // After renegotiate(), Hello is sent again.
        svc.renegotiate("bob")
        svc.initiate("bob")

        assertEquals(2, sender.unicasts.size)
    }

    @Test
    fun getAllNegotiated_listsLockedInPeers() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        svc.handleHello(
            newHelloPacket("bob", "alice", HelloPayload(1, 2, listOf("signal-x3dh"), "")),
        )
        svc.handleHello(
            newHelloPacket("carol", "alice", HelloPayload(1, 2, listOf("voice"), "")),
        )

        val all = svc.getAllNegotiated()
        assertEquals(2, all.size)
        assertEquals(setOf("bob", "carol"), all.map { it.peerUhid }.toSet())
    }

    @Test
    fun assumeLegacyV1_installsFallbackOnce() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        var fired = 0
        svc.onPeerNegotiated = { fired++ }

        svc.assumeLegacyV1("ghost")
        svc.assumeLegacyV1("ghost") // idempotent

        assertEquals(1, fired)
        val caps = svc.getPeerCapabilities("ghost")
        assertNotNull(caps)
        assertEquals(1.toByte(), caps!!.negotiatedVersion)
        assertTrue(caps.capabilities.isEmpty())
    }

    @Test
    fun assumeLegacyV1_doesNotOverrideExistingNegotiation() = runBlocking {
        val sender = FakeMeshSender("alice")
        val svc = HandshakeService(sender)

        // Real handshake first.
        svc.handleHello(
            newHelloPacket("bob", "alice", HelloPayload(1, 2, listOf("signal-x3dh"), "ok")),
        )
        // Then a (rare) timeout fallback for the same peer — must be ignored.
        svc.assumeLegacyV1("bob")

        val caps = svc.getPeerCapabilities("bob")
        assertNotNull(caps)
        assertEquals(2.toByte(), caps!!.negotiatedVersion)
        assertTrue("signal-x3dh" in caps.capabilities)
    }

    // ─── JSON wire shape ───────────────────────────────────────────────────

    @Test
    fun helloPayload_jsonShapeMatchesSnakeCase() {
        val p = HelloPayload(
            minVersion = 1, maxVersion = 2,
            capabilities = listOf("signal-x3dh", "voice"),
            implementation = "aether-kotlin/1.0.0",
        )
        val json = String(p.toJsonBytes(), Charsets.UTF_8)

        assertTrue(json.contains("\"min_version\":1"))
        assertTrue(json.contains("\"max_version\":2"))
        assertTrue(json.contains("\"capabilities\":[\"signal-x3dh\",\"voice\"]"))
        assertTrue(json.contains("\"implementation\":\"aether-kotlin/1.0.0\""))
    }

    @Test
    fun helloPayload_roundTripJsonPreservesAllFields() {
        val original = HelloPayload(
            minVersion = 1, maxVersion = 5,
            capabilities = listOf("signal-x3dh", "double-ratchet", "voice"),
            implementation = "aether-csharp/1.0.0",
        )
        val bytes = original.toJsonBytes()
        val decoded = HelloPayload.fromJsonBytesOrNull(bytes)

        assertNotNull(decoded)
        assertEquals(original.minVersion, decoded!!.minVersion)
        assertEquals(original.maxVersion, decoded.maxVersion)
        assertEquals(original.capabilities, decoded.capabilities)
        assertEquals(original.implementation, decoded.implementation)
    }

    @Test
    fun helloPayload_acceptsKnownCSharpJsonShape() {
        // Exact byte shape a C# peer would emit.
        val csharpJson = """{"min_version":1,"max_version":2,"capabilities":["signal-x3dh","double-ratchet","dtn-custody"],"implementation":"aether-csharp/1.0.0"}"""
        val decoded = HelloPayload.fromJsonBytesOrNull(csharpJson.toByteArray(Charsets.UTF_8))

        assertNotNull(decoded)
        assertEquals(1.toByte(), decoded!!.minVersion)
        assertEquals(2.toByte(), decoded.maxVersion)
        assertContentEquals(
            listOf("signal-x3dh", "double-ratchet", "dtn-custody"),
            decoded.capabilities,
        )
        assertEquals("aether-csharp/1.0.0", decoded.implementation)
    }

    @Test
    fun helloPayload_toleratesUnknownFieldsAndWhitespace() {
        val odd = """
            {
              "min_version": 1,
              "max_version": 2,
              "capabilities": ["signal-x3dh"],
              "implementation": "x",
              "future_extension": {"x": 1}
            }
        """.trimIndent()
        val decoded = HelloPayload.fromJsonBytesOrNull(odd.toByteArray(Charsets.UTF_8))
        assertNotNull(decoded)
        assertEquals(1.toByte(), decoded!!.minVersion)
        assertEquals(2.toByte(), decoded.maxVersion)
        assertEquals(listOf("signal-x3dh"), decoded.capabilities)
    }

    @Test
    fun helloPayload_emptyOrInvalidReturnsNull() {
        assertNull(HelloPayload.fromJsonBytesOrNull(null))
        assertNull(HelloPayload.fromJsonBytesOrNull(ByteArray(0)))
        assertNull(HelloPayload.fromJsonBytesOrNull("not-json".toByteArray()))
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private fun newHelloPacket(from: String, to: String, payload: HelloPayload): MeshPacket =
        MeshPacket(
            type = PacketType.Hello,
            sourceUhid = from,
            destinationUhid = to,
            ttl = 1,
            payload = payload.toJsonBytes(),
        )

    private fun newAckPacket(from: String, to: String, payload: HelloPayload): MeshPacket =
        MeshPacket(
            type = PacketType.HelloAck,
            sourceUhid = from,
            destinationUhid = to,
            ttl = 1,
            payload = payload.toJsonBytes(),
        )
}
