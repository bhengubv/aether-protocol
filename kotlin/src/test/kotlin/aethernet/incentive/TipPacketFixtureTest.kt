// SPDX-License-Identifier: MIT

package aethernet.incentive

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Cross-language tipping parity: the Kotlin port must reproduce the C# reference vectors
 * (fixtures/tipping/tip_packet_basic.json) byte-for-byte. Mirrors the Go
 * tip_packet_fixture_test.go suite. Any drift between Kotlin and the other ports surfaces here as a
 * hex mismatch (canonical_bytes) or a signature mismatch.
 */
class TipPacketFixtureTest {

    private data class TipCase(
        val tipperUhid: String,
        val recipientUhid: String,
        val amount: String,
        val trafficType: String,
        val referenceId: String?,
        val timestampUnixMs: Long,
        val canonicalBytes: String,
        val signature: String,
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
        JSONObject(File(repoRoot(), "fixtures/tipping/tip_packet_basic.json").readText())

    private fun cases(v: JSONObject): List<TipCase> {
        val arr = v.getJSONArray("cases")
        return (0 until arr.length()).map { i ->
            val o = arr.getJSONObject(i)
            TipCase(
                tipperUhid = o.getString("tipper_uhid"),
                recipientUhid = o.getString("recipient_uhid"),
                amount = o.getString("amount"),
                trafficType = o.getString("traffic_type"),
                referenceId = if (o.isNull("reference_id")) null else o.getString("reference_id"),
                timestampUnixMs = o.getLong("timestamp_unix_ms"),
                canonicalBytes = o.getString("canonical_bytes"),
                signature = o.getString("signature"),
            )
        }
    }

    private fun caseToPayload(c: TipCase): TipPacketPayload = TipPacketPayload(
        tipperUhid = c.tipperUhid,
        recipientUhid = c.recipientUhid,
        amount = c.amount,
        trafficType = c.trafficType,
        referenceId = c.referenceId?.let { UUID.fromString(it) },
        timestampUnixMs = c.timestampUnixMs,
    )

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun unhex(s: String): ByteArray {
        if (s.isEmpty()) return ByteArray(0)
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    private fun privFromSeed(seed: ByteArray) = Ed25519PrivateKeyParameters(seed, 0)

    private fun signWithSeed(priv: Ed25519PrivateKeyParameters, data: ByteArray): ByteArray {
        val signer = Ed25519Signer()
        signer.init(true, priv)
        signer.update(data, 0, data.size)
        return signer.generateSignature()
    }

    // ── canonical bytes ────────────────────────────────────────────────────────

    /**
     * Asserts buildCanonicalData reproduces the fixture canonical_bytes byte-for-byte for every case
     * (covers null reference_id → 16 zero bytes, the .NET mixed-endian GUID byte order, the invariant
     * decimal amount string, the LE length prefixes, and the i64 LE timestamp).
     */
    @Test
    fun `tip canonical bytes parity with the C# reference fixture`() {
        for ((i, c) in cases(vectors()).withIndex()) {
            val got = hex(caseToPayload(c).buildCanonicalData())
            assertEquals(c.canonicalBytes, got, "case $i (${c.tipperUhid}): canonical bytes mismatch")
        }
    }

    /**
     * Asserts a fresh Ed25519 sign from the fixture seed reproduces the fixture signature exactly
     * (Ed25519 is deterministic), the derived public key matches the fixture, and the fixture
     * signature verifies against the fixture public key.
     */
    @Test
    fun `tip deterministic signature parity with the C# reference fixture`() {
        val v = vectors()
        val seed = unhex(v.getString("ed25519_seed"))
        assertEquals(32, seed.size, "seed size")
        val priv = privFromSeed(seed)
        val pub = priv.generatePublicKey()

        // The derived public key must match the fixture's published key.
        assertEquals(v.getString("public_key"), hex(pub.encoded), "public key")

        for ((i, c) in cases(v).withIndex()) {
            val canonical = caseToPayload(c).buildCanonicalData()

            // Deterministic re-sign reproduces the exact fixture signature.
            val sig = signWithSeed(priv, canonical)
            assertEquals(c.signature, hex(sig), "case $i (${c.tipperUhid}): signature mismatch")

            // The fixture signature verifies against the fixture public key.
            val verifier = Ed25519Signer()
            verifier.init(false, Ed25519PublicKeyParameters(pub.encoded, 0))
            verifier.update(canonical, 0, canonical.size)
            assertTrue(verifier.verifySignature(unhex(c.signature)), "case $i: fixture signature failed to verify")
        }
    }

    /** Proves a signed payload survives a JSON round-trip with canonical bytes and signature intact. */
    @Test
    fun `tip payload survives JSON round-trip`() {
        val v = vectors()
        val priv = privFromSeed(unhex(v.getString("ed25519_seed")))

        for ((i, c) in cases(v).withIndex()) {
            val signed = caseToPayload(c).copy(signature = signWithSeed(priv, caseToPayload(c).buildCanonicalData()))

            val back = assertNotNull(TipPacketPayload.fromJson(signed.toJson()), "case $i: parse failed")

            assertContentEquals(signed.buildCanonicalData(), back.buildCanonicalData(), "case $i: canonical bytes changed")
            assertContentEquals(signed.signature, back.signature, "case $i: signature changed")
            assertEquals(c.amount, back.amount, "case $i: amount changed")
            // reference_id presence/absence must survive.
            assertEquals(signed.referenceId, back.referenceId, "case $i: reference_id changed")
        }
    }

    /** BigDecimal.of(...) must canonicalise to the same invariant decimal string the fixture carries. */
    @Test
    fun `tip BigDecimal factory canonicalises to fixture amount string`() {
        for (c in cases(vectors())) {
            val viaBigDecimal = TipPacketPayload.of(
                tipperUhid = c.tipperUhid,
                recipientUhid = c.recipientUhid,
                amount = java.math.BigDecimal(c.amount),
                trafficType = c.trafficType,
                referenceId = c.referenceId?.let { UUID.fromString(it) },
                timestampUnixMs = c.timestampUnixMs,
            )
            assertEquals(c.amount, viaBigDecimal.amount, "BigDecimal amount canonicalisation for ${c.amount}")
            assertEquals(c.canonicalBytes, hex(viaBigDecimal.buildCanonicalData()), "BigDecimal path canonical bytes")
        }
    }

    // ── service dispatch ─────────────────────────────────────────────────────────

    private class FakeSender(override val localUhid: String) : MeshTipService.MeshSender {
        val sent = mutableListOf<MeshPacket>()
        val broadcasts = mutableListOf<MeshPacket>()
        override suspend fun send(packet: MeshPacket, nextHopUhid: String): Boolean { sent += packet; return true }
        override suspend fun broadcast(packet: MeshPacket): Int { broadcasts += packet; return 1 }
    }

    private class FakeSigner : MeshTipService.PacketSigner {
        override fun sign(packet: MeshPacket) {
            packet.signature = "envelope-sig".toByteArray()
            packet.packetNonce = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        }
    }

    private class SeedIdentity(private val priv: Ed25519PrivateKeyParameters) : MeshTipService.IdentitySigner {
        override fun signData(data: ByteArray): ByteArray {
            val signer = Ed25519Signer()
            signer.init(true, priv)
            signer.update(data, 0, data.size)
            return signer.generateSignature()
        }
    }

    private class RecordingSettler : MeshTipService.MeshTipSettlementProvider {
        val calls = mutableListOf<TipPacketPayload>()
        override suspend fun settleMeshTip(payload: TipPacketPayload) { calls += payload }
    }

    /**
     * Wires the full MeshTipService send path with the fixture seed and confirms the signed payload
     * inside the emitted TipPacket(24) carries the exact fixture signature — proving the service-level
     * flow is byte-identical to C#.
     */
    @Test
    fun `sendTip produces the fixture signature inside an emitted TipPacket`() = runBlocking {
        val v = vectors()
        val priv = privFromSeed(unhex(v.getString("ed25519_seed")))
        val c = cases(v)[0]

        val sender = FakeSender(c.tipperUhid)
        val svc = MeshTipService(sender, FakeSigner(), SeedIdentity(priv), routing = null, settle = null)

        val signed = svc.sendTip(
            recipientUhid = c.recipientUhid,
            amount = c.amount,
            trafficType = c.trafficType,
            referenceId = c.referenceId?.let { UUID.fromString(it) },
            timestampUnixMs = c.timestampUnixMs,
        )
        assertEquals(PacketType.TipPacket, signed.type, "emitted packet type")

        val payload = assertNotNull(TipPacketPayload.fromJson(signed.payload.toString(Charsets.UTF_8)))
        assertEquals(c.signature, hex(payload.signature!!), "service-emitted signature mismatch")

        // With no route resolver, the tip must have been broadcast.
        assertEquals(1, sender.broadcasts.size, "expected 1 broadcast")
        assertEquals(0, sender.sent.size, "expected 0 unicast")
    }

    /**
     * Proves an inbound TipPacket(24) is dispatched to the host settlement hook (the Kotlin analog of
     * IAetherNetIncentiveProvider.SettleMeshTip), and a packet with a malformed signature is dropped
     * before the hook fires.
     */
    @Test
    fun `handleTipPacket routes to the settlement hook and drops a malformed signature`() = runBlocking {
        val v = vectors()
        val priv = privFromSeed(unhex(v.getString("ed25519_seed")))
        val c = cases(v)[0]

        // Local node is the addressed recipient, so no onward relay happens.
        val sender = FakeSender(c.recipientUhid)
        val settler = RecordingSettler()
        val svc = MeshTipService(sender, FakeSigner(), SeedIdentity(priv), routing = null, settle = settler)

        val identity = SeedIdentity(priv)
        val signedPayload = caseToPayload(c).copy(signature = identity.signData(caseToPayload(c).buildCanonicalData()))
        val pkt = MeshPacket(
            type = PacketType.TipPacket,
            sourceUhid = c.tipperUhid,
            destinationUhid = c.recipientUhid,
            payload = signedPayload.toJson().toByteArray(Charsets.UTF_8),
        )

        assertTrue(svc.handleTipPacket(pkt), "expected the tip to be handled")
        assertEquals(1, settler.calls.size, "settlement hook should fire once")
        assertEquals(c.tipperUhid, settler.calls[0].tipperUhid, "settlement hook got wrong payload")

        // A malformed signature (wrong length) must be dropped before the hook fires.
        settler.calls.clear()
        val badPayload = signedPayload.copy(signature = byteArrayOf(0x00, 0x01, 0x02))
        val badPkt = MeshPacket(
            type = PacketType.TipPacket,
            sourceUhid = c.tipperUhid,
            destinationUhid = c.recipientUhid,
            payload = badPayload.toJson().toByteArray(Charsets.UTF_8),
        )
        assertFalse(svc.handleTipPacket(badPkt), "a malformed-signature tip must be dropped")
        assertTrue(settler.calls.isEmpty(), "settlement hook must NOT fire for a malformed-signature tip")
    }

    /** Confirms the default no-op settlement provider settles nothing without error. */
    @Test
    fun `noop settlement provider settles nothing`() = runBlocking {
        MeshTipService.NoopMeshTipSettlementProvider().settleMeshTip(TipPacketPayload())
    }

    /** A null reference_id must serialise to 16 zero bytes in the GUID slot of the canonical data. */
    @Test
    fun `null reference id is sixteen zero bytes`() {
        val nullRefCase = cases(vectors()).first { it.referenceId == null }
        val canonical = caseToPayload(nullRefCase).buildCanonicalData()
        // Locate the 16-byte GUID slot: it is the 16 bytes immediately before the trailing 8-byte timestamp.
        val guidStart = canonical.size - 8 - 16
        val guidSlot = canonical.copyOfRange(guidStart, guidStart + 16)
        assertTrue(guidSlot.all { it.toInt() == 0 }, "null reference_id must be 16 zero bytes")
        assertNull(caseToPayload(nullRefCase).referenceId)
    }
}
