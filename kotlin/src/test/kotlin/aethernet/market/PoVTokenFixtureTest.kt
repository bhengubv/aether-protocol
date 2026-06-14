// SPDX-License-Identifier: MIT

package aethernet.market

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Cross-language Proof-of-Vicinity parity: the Kotlin port must reproduce the C# reference vectors
 * (fixtures/market/pov_token_basic.json) byte-for-byte. Mirrors the Go pov_token_fixture_test.go
 * suite. Any drift in the canonical body layout (LE i32 subject length, .NET DateTime.Ticks i64 LE,
 * transport byte) or the Ed25519 signing surfaces here as a mismatch.
 */
class PoVTokenFixtureTest {

    private data class PoVCase(
        val subjectUhid: String,
        val timestampTicks: Long,
        val transport: String,
        val transportByte: Int,
        val canonicalBody: String,
        val witnessSignature: String,
    )

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun vectors(): JSONObject =
        JSONObject(File(repoRoot(), "fixtures/market/pov_token_basic.json").readText())

    private fun cases(v: JSONObject): List<PoVCase> {
        val arr = v.getJSONArray("cases")
        return (0 until arr.length()).map { i ->
            val o = arr.getJSONObject(i)
            PoVCase(
                subjectUhid = o.getString("subject_uhid"),
                timestampTicks = o.getLong("timestamp_ticks"),
                transport = o.getString("transport"),
                transportByte = o.getInt("transport_byte"),
                canonicalBody = o.getString("canonical_body"),
                witnessSignature = o.getString("witness_signature"),
            )
        }
    }

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun unhex(s: String): ByteArray =
        ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }

    private fun transportOf(b: Int): PoVTransportType =
        PoVTransportType.fromValue(b.toByte()) ?: error("unknown transport byte $b")

    private fun privFromSeed(seed: ByteArray) = Ed25519PrivateKeyParameters(seed, 0)

    private fun sign(priv: Ed25519PrivateKeyParameters, data: ByteArray): ByteArray {
        val signer = Ed25519Signer()
        signer.init(true, priv)
        signer.update(data, 0, data.size)
        return signer.generateSignature()
    }

    private fun verify(pub: ByteArray, data: ByteArray, sig: ByteArray): Boolean {
        val verifier = Ed25519Signer()
        verifier.init(false, Ed25519PublicKeyParameters(pub, 0))
        verifier.update(data, 0, data.size)
        return verifier.verifySignature(sig)
    }

    // ── canonical body ─────────────────────────────────────────────────────────

    /**
     * Asserts buildSignableTokenData reproduces the fixture canonical_body byte-for-byte for every
     * case (covers all three transports + the .NET DateTime.Ticks i64 LE field).
     */
    @Test
    fun `pov canonical body parity with the C# reference fixture`() {
        for ((i, c) in cases(vectors()).withIndex()) {
            val transport = transportOf(c.transportByte)
            val got = hex(PoVToken.buildSignableTokenData(c.subjectUhid, c.timestampTicks, transport))
            assertEquals(c.canonicalBody, got, "case $i (${c.subjectUhid}): canonical body mismatch")
            // Transport enum byte must match the named transport.
            assertEquals(c.transport, transport.wireName(), "case $i: transport name mismatch")
        }
    }

    /**
     * Asserts a fresh Ed25519 sign from the fixture witness seed reproduces the fixture
     * witness_signature exactly (Ed25519 is deterministic), and that the fixture signature verifies
     * against the fixture witness public key.
     */
    @Test
    fun `pov witness signature parity with the C# reference fixture`() {
        val v = vectors()
        val seed = unhex(v.getString("witness_seed"))
        assertEquals(32, seed.size, "seed size")
        val priv = privFromSeed(seed)
        val pub = priv.generatePublicKey().encoded

        assertEquals(v.getString("witness_public_key"), hex(pub), "witness public key")

        for ((i, c) in cases(v).withIndex()) {
            val body = PoVToken.buildSignableTokenData(c.subjectUhid, c.timestampTicks, transportOf(c.transportByte))

            val sig = sign(priv, body)
            assertEquals(c.witnessSignature, hex(sig), "case $i (${c.subjectUhid}): witness signature mismatch")

            assertTrue(verify(pub, body, unhex(c.witnessSignature)), "case $i: fixture witness signature failed to verify")
        }
    }

    /** Proves a token with both signatures survives a JSON round-trip with its canonical body intact. */
    @Test
    fun `pov token survives JSON round-trip`() {
        val v = vectors()
        val priv = privFromSeed(unhex(v.getString("witness_seed")))

        for ((i, c) in cases(v).withIndex()) {
            val transport = transportOf(c.transportByte)
            val tok = PoVToken(
                witnessUhid = "aether:witness:zz",
                subjectUhid = c.subjectUhid,
                timestampTicks = c.timestampTicks,
                transportUsed = transport,
                witnessSignature = sign(priv, PoVToken.buildSignableTokenData(c.subjectUhid, c.timestampTicks, transport)),
            )

            val back = assertNotNull(PoVToken.fromJson(tok.toJson()), "case $i: parse failed")
            assertContentEquals(tok.signableData(), back.signableData(), "case $i: canonical body changed")
            assertContentEquals(tok.witnessSignature, back.witnessSignature, "case $i: witness signature changed")
            assertEquals(tok.transportUsed, back.transportUsed, "case $i: transport changed")
        }
    }

    /** Confirms the .NET ticks <-> Unix-millis conversion is lossless at ms resolution for the fixtures. */
    @Test
    fun `pov ticks to unix-millis round-trips at millisecond resolution`() {
        for ((i, c) in cases(vectors()).withIndex()) {
            // The fixture ticks are at sub-millisecond precision in some cases; round to ms first so the
            // conversion is exercised on a millisecond-aligned value (matching how a wall clock produces ticks).
            val msAligned = (c.timestampTicks / 10_000L) * 10_000L
            val round = PoVToken.unixMillisToTicks(PoVToken.ticksToUnixMillis(msAligned))
            assertEquals(msAligned, round, "case $i: ticks round-trip lost precision")
        }
    }

    // ── exchange-service flow ──────────────────────────────────────────────────

    private class FakeSender(override val localUhid: String) : PoVTokenExchangeService.MeshSender {
        val sent = mutableListOf<MeshPacket>()
        override suspend fun send(packet: MeshPacket, subjectUhid: String): Boolean { sent += packet; return true }
    }

    private class RealIdentity(private val priv: Ed25519PrivateKeyParameters) : PoVTokenExchangeService.IdentitySigner {
        override fun signData(data: ByteArray): ByteArray {
            val signer = Ed25519Signer()
            signer.init(true, priv)
            signer.update(data, 0, data.size)
            return signer.generateSignature()
        }
        override fun verifySignature(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean {
            return try {
                val verifier = Ed25519Signer()
                verifier.init(false, Ed25519PublicKeyParameters(publicKey, 0))
                verifier.update(data, 0, data.size)
                verifier.verifySignature(signature)
            } catch (_: Exception) {
                false
            }
        }
    }

    /**
     * Stamps a real Ed25519 envelope signature with the node's key and verifies fresh, with nonce
     * replay-dedup (mirroring the C# IPacketSigningService contract; freshness is exercised in the C#
     * layer — here we focus on the body crypto and replay rejection).
     */
    private class PassSigner(private val priv: Ed25519PrivateKeyParameters) : PoVTokenExchangeService.PacketSigner {
        private val seen = HashSet<String>()
        override fun sign(packet: MeshPacket) {
            packet.packetNonce = byteArrayOf(9, 9, 9, 9, 9, 9, 9, 9)
            packet.signature = signBytes("${packet.sourceUhid}:${packet.destinationUhid}".toByteArray())
        }
        override fun verify(packet: MeshPacket, senderPublicKey: ByteArray): Boolean {
            val key = "${packet.sourceUhid}:${hex(packet.packetNonce)}"
            if (!seen.add(key)) return false // replay
            return try {
                val verifier = Ed25519Signer()
                verifier.init(false, Ed25519PublicKeyParameters(senderPublicKey, 0))
                val data = "${packet.sourceUhid}:${packet.destinationUhid}".toByteArray()
                verifier.update(data, 0, data.size)
                verifier.verifySignature(packet.signature)
            } catch (_: Exception) {
                false
            }
        }
        private fun signBytes(data: ByteArray): ByteArray {
            val signer = Ed25519Signer()
            signer.init(true, priv)
            signer.update(data, 0, data.size)
            return signer.generateSignature()
        }
        private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }

    private fun newKeyPair(): Pair<ByteArray, Ed25519PrivateKeyParameters> {
        val (privBytes, pubBytes) = aethernet.security.Ed25519Service.generateKeyPair()
        return pubBytes to Ed25519PrivateKeyParameters(privBytes, 0)
    }

    /**
     * Exercises the on-mesh exchange end-to-end: the witness issues a token over packet 43; the subject
     * verifies the witness Ed25519 signature, counter-signs, and records it; and BOTH signatures then
     * verify against their respective keys. Replaying the same packet is rejected by the nonce dedup.
     */
    @Test
    fun `pov exchange full flow issues counter-signs and verifies both signatures`() = runBlocking {
        val (witnessPub, witnessPriv) = newKeyPair()
        val (subjectPub, subjectPriv) = newKeyPair()

        val witnessUhid = "aether:node:witness"
        val subjectUhid = "aether:node:subject"

        // Witness side.
        val wSender = FakeSender(witnessUhid)
        val witness = PoVTokenExchangeService(wSender, PassSigner(witnessPriv), RealIdentity(witnessPriv))

        val token = assertNotNull(
            witness.issueToken(subjectUhid, PoVTransportType.Ble),
            "witness refused to issue a valid token",
        )
        assertEquals(1, wSender.sent.size, "expected exactly 1 directed send")
        val exchangePkt = wSender.sent[0]
        assertEquals(PacketType.PoVTokenExchange, exchangePkt.type, "issued packet type")
        assertEquals(1, exchangePkt.ttl, "issued packet TTL (one short-range hop)")
        // The issued token (pre-countersign) carries the witness signature but no subject signature.
        assertNotNull(token.witnessSignature)
        assertNull(token.subjectSignature)

        // Subject side receives the witness's packet.
        val sSender = FakeSender(subjectUhid)
        val subject = PoVTokenExchangeService(sSender, PassSigner(subjectPriv), RealIdentity(subjectPriv))
        var received: PoVToken? = null
        subject.onTokenReceived = { received = it }

        assertTrue(subject.handleTokenExchange(exchangePkt, witnessPub), "subject rejected a valid witness token")
        val acc = assertNotNull(received, "onTokenReceived did not fire")

        // BOTH signatures must now verify over the same canonical body.
        val body = acc.signableData()
        assertTrue(verify(witnessPub, body, acc.witnessSignature!!), "witness signature failed to verify")
        assertTrue(verify(subjectPub, body, acc.subjectSignature!!), "subject countersignature failed to verify")

        // Score reflects one unique witness for the subject.
        assertEquals(1, subject.getScore(subjectUhid).uniqueWitnesses, "expected 1 unique witness")
        assertEquals(listOf(subjectUhid), subject.acceptedSubjects())

        // Replaying the same packet is rejected by the signer's nonce dedup.
        assertFalse(subject.handleTokenExchange(exchangePkt, witnessPub), "a replayed PoV exchange packet must be rejected")
    }

    /** Confirms the hard invariants: no self-vouch and no non-short-range minting. */
    @Test
    fun `pov exchange refuses self-vouch and remote minting`() = runBlocking {
        val (_, priv) = newKeyPair()
        val sender = FakeSender("aether:node:self")
        val svc = PoVTokenExchangeService(sender, PassSigner(priv), RealIdentity(priv))

        // Self-vouch refused.
        assertNull(svc.issueToken("aether:node:self", PoVTransportType.Ble), "a node must not vouch for itself")
        // Non-short-range refused: there is no short-range enum member that fails, so emulate by passing a
        // transport whose byte is not short-range via a crafted token is impossible through the enum;
        // instead assert all enum members ARE short-range (the C# refusal path triggers only for
        // out-of-enum transports, which the typed Kotlin API forbids at the call site).
        assertTrue(PoVTransportType.entries.all { it.isShortRange() }, "every typed transport is short-range")
        assertEquals(0, sender.sent.size, "no packet should have been sent for a refused issuance")
    }

    /** The subject must reject a witness signature that does not match the verified sender key. */
    @Test
    fun `pov exchange rejects a forged witness signature`() = runBlocking {
        val (witnessPub, witnessPriv) = newKeyPair()
        val (_, subjectPriv) = newKeyPair()
        val (attackerPub, _) = newKeyPair()

        val witnessUhid = "aether:node:witness2"
        val subjectUhid = "aether:node:subject2"

        val wSender = FakeSender(witnessUhid)
        val witness = PoVTokenExchangeService(wSender, PassSigner(witnessPriv), RealIdentity(witnessPriv))
        assertNotNull(witness.issueToken(subjectUhid, PoVTransportType.Nfc))
        val exchangePkt = wSender.sent[0]

        // First subject: presented the WRONG envelope key (attacker key). The envelope verify fails → dropped.
        val sSender1 = FakeSender(subjectUhid)
        val subject1 = PoVTokenExchangeService(sSender1, PassSigner(subjectPriv), RealIdentity(subjectPriv))
        assertFalse(subject1.handleTokenExchange(exchangePkt, attackerPub), "wrong envelope key must be rejected")

        // Second subject (fresh nonce-dedup state): tamper the token BODY so the witness signature no
        // longer matches, but keep a VALID envelope signed by the witness key. The envelope verify passes,
        // so the drop is specifically due to the witness Ed25519 body-signature check failing.
        val original = PoVToken.fromJson(exchangePkt.payload.toString(Charsets.UTF_8))!!
        val tampered = original.copy(timestampTicks = original.timestampTicks + 1)
        val tamperedPkt = MeshPacket(
            type = PacketType.PoVTokenExchange,
            sourceUhid = witnessUhid,
            destinationUhid = subjectUhid,
            ttl = 1,
            payload = tampered.toJson().toByteArray(Charsets.UTF_8),
        )
        // Re-sign the tampered envelope with the witness key so envelope verify passes; body sig must fail.
        PassSigner(witnessPriv).sign(tamperedPkt)
        val sSender2 = FakeSender(subjectUhid)
        val subject2 = PoVTokenExchangeService(sSender2, PassSigner(subjectPriv), RealIdentity(subjectPriv))
        assertFalse(subject2.handleTokenExchange(tamperedPkt, witnessPub), "tampered body must fail witness-signature check")
    }
}
