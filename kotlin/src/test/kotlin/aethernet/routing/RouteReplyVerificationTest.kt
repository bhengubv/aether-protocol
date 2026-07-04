// SPDX-License-Identifier: MIT
package aethernet.routing

import aethernet.AetherNetConstants
import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.security.Ed25519RouteReplyVerifier
import aethernet.security.Ed25519Service
import aethernet.security.PacketSigning
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * Security acceptance tests for fail-closed RREP verification (Gap 3) — the Kotlin mirror of
 * `RouteReplyVerificationTests.cs`.
 *
 * Proves the properties of the hardened routing layer:
 *   (a) a RoutingService with NO verifier supplied REJECTS an RREP — no forward route installed;
 *   (b) an [Ed25519RouteReplyVerifier] whose resolver returns the correct public key ACCEPTS a
 *       validly-signed RREP — forward route installed;
 *   (c) a forged RREP (signed by a DIFFERENT key), an unsigned RREP, and an RREP from an unknown
 *       signer are ALL rejected.
 *
 * Signed RREPs are built with a real Ed25519 keypair via the production signing path
 * ([PacketSigning.signPacket]) over the canonical signable bytes, so this exercises the actual
 * signature verification, not a stub. Assertions are on the observable side effect:
 * presence/absence of the forward route in the store.
 */
class RouteReplyVerificationTest {

    private val local = "local-uhid"
    private val source = "carol"

    private fun newRrep(src: String = source, dest: String = local, ttl: Int = AetherNetConstants.DEFAULT_TTL) =
        MeshPacket(type = PacketType.RouteReply, sourceUhid = src, destinationUhid = dest, ttl = ttl)

    /** Signs [rrep] in place with [privateKey], filling its Ed25519 signature. Returns the same packet. */
    private fun sign(rrep: MeshPacket, privateKey: ByteArray): MeshPacket {
        rrep.signature = PacketSigning.signPacket(rrep, privateKey)
        return rrep
    }

    /** Minimal in-test UHID→public-key map for the routing verifier (mirrors the C# StubKeyResolver). */
    private class StubKeyResolver(uhid: String? = null, publicKey: ByteArray? = null) : RouteReplyKeyResolver {
        private val keys = HashMap<String, ByteArray>()
        init { if (uhid != null && publicKey != null) keys[uhid] = publicKey }
        override fun resolvePublicKey(sourceUhid: String): ByteArray? = keys[sourceUhid]
    }

    // ─── (a) No verifier ⇒ fail-closed reject ────────────────────────────────

    @Test
    fun `no verifier rejects rrep - no route installed`() = runBlocking {
        val sender = FakeMeshSender(local)
        val store = InMemoryRouteStore()
        // No verifier argument at all — the fail-closed default (RejectAll) must apply.
        val svc = RoutingService(sender, store)

        svc.handleRouteReply(newRrep())

        assertNull(store.get(source)) // route rejected — not installed
        assertNull(svc.getCachedRoute(source))
    }

    // ─── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ───────

    @Test
    fun `ed25519 verifier installs forward route for validly-signed rrep`() = runBlocking {
        val sender = FakeMeshSender(local)
        val store = InMemoryRouteStore()

        // The source node's real identity. Its public key is registered with the resolver.
        val (sourcePriv, sourcePub) = Ed25519Service.generateKeyPair()
        val resolver = StubKeyResolver(source, sourcePub)
        val verifier = Ed25519RouteReplyVerifier(resolver)
        val svc = RoutingService(sender, store, verifier)

        svc.handleRouteReply(sign(newRrep(), sourcePriv))

        val route = store.get(source)
        assertNotNull(route)
        assertEquals(source, route.nextHopUhid)
    }

    // ─── (c) Forged (wrong-key) signature ⇒ reject ───────────────────────────

    @Test
    fun `ed25519 verifier rejects forged rrep signed by different key`() = runBlocking {
        val sender = FakeMeshSender(local)
        val store = InMemoryRouteStore()

        // Resolver knows the LEGITIMATE source key...
        val (_, legitPub) = Ed25519Service.generateKeyPair()
        val resolver = StubKeyResolver(source, legitPub)
        val verifier = Ed25519RouteReplyVerifier(resolver)
        val svc = RoutingService(sender, store, verifier)

        // ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
        val (attackerPriv, _) = Ed25519Service.generateKeyPair()

        svc.handleRouteReply(sign(newRrep(), attackerPriv))

        assertNull(store.get(source)) // forged signature rejected — no route
    }

    // ─── (c) Unsigned RREP ⇒ reject ──────────────────────────────────────────

    @Test
    fun `ed25519 verifier rejects unsigned rrep`() = runBlocking {
        val sender = FakeMeshSender(local)
        val store = InMemoryRouteStore()

        val (_, sourcePub) = Ed25519Service.generateKeyPair()
        val resolver = StubKeyResolver(source, sourcePub)
        val verifier = Ed25519RouteReplyVerifier(resolver)
        val svc = RoutingService(sender, store, verifier)

        // RREP with an empty signature (the MeshPacket default) — must be rejected.
        svc.handleRouteReply(newRrep())

        assertNull(store.get(source))
    }

    // ─── (c') Unknown signer (resolver returns null) ⇒ reject ────────────────

    @Test
    fun `ed25519 verifier rejects unknown source`() = runBlocking {
        val sender = FakeMeshSender(local)
        val store = InMemoryRouteStore()

        // Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
        val resolver = StubKeyResolver() // empty
        val verifier = Ed25519RouteReplyVerifier(resolver)
        val svc = RoutingService(sender, store, verifier)

        val (sourcePriv, _) = Ed25519Service.generateKeyPair()

        svc.handleRouteReply(sign(newRrep(), sourcePriv))

        assertNull(store.get(source))
    }
}
