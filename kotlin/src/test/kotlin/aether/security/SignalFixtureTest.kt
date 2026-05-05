// SPDX-License-Identifier: MIT
package aether.security

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.File
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Cross-language Signal-protocol fixture verifier and end-to-end exercises.
 *
 * Verifies that the Kotlin implementation produces byte-identical X3DH and
 * ratchet outputs to the C# reference (committed in
 * fixtures/signal/expected/*.json). Any drift between Kotlin and the other
 * 7 languages surfaces here as a hex mismatch.
 */
class SignalFixtureTest {

    // ─── Fixture verifiers ──────────────────────────────────────────────────

    @Test
    fun `signal fixture x3dh_basic`() {
        val (inputs, expected) = loadFixturePair("x3dh_basic")

        val aliceIK = unhex(inputs["alice_identity_priv_hex"]!!)
        val aliceEK = unhex(inputs["alice_ephemeral_priv_hex"]!!)
        val bobIK = unhex(inputs["bob_identity_priv_hex"]!!)
        val bobSPK = unhex(inputs["bob_signed_pre_key_priv_hex"]!!)
        val bobOPK = unhex(inputs["bob_one_time_pre_key_priv_hex"]!!)

        val aliceIKPub = x25519DerivePub(aliceIK)
        val aliceEKPub = x25519DerivePub(aliceEK)
        val bobIKPub = x25519DerivePub(bobIK)
        val bobSPKPub = x25519DerivePub(bobSPK)
        val bobOPKPub = x25519DerivePub(bobOPK)

        val dh1 = x25519AgreeStandalone(aliceIK, bobSPKPub)
        val dh2 = x25519AgreeStandalone(aliceEK, bobIKPub)
        val dh3 = x25519AgreeStandalone(aliceEK, bobSPKPub)
        val dh4 = x25519AgreeStandalone(aliceEK, bobOPKPub)

        val shared = dh1 + dh2 + dh3 + dh4
        val rootInfo = inputs["hkdf_root_info_utf8"]!!.toByteArray(Charsets.UTF_8)
        val sendInfo = inputs["hkdf_chain_initiator_send_info_utf8"]!!.toByteArray(Charsets.UTF_8)
        val recvInfo = inputs["hkdf_chain_initiator_recv_info_utf8"]!!.toByteArray(Charsets.UTF_8)

        val rootKey = hkdf32Standalone(shared, rootInfo)
        val sendChain = hkdf32Standalone(rootKey, sendInfo)
        val recvChain = hkdf32Standalone(rootKey, recvInfo)

        assertEquals(expected["alice_identity_pub_hex"], hex(aliceIKPub))
        assertEquals(expected["alice_ephemeral_pub_hex"], hex(aliceEKPub))
        assertEquals(expected["bob_identity_pub_hex"], hex(bobIKPub))
        assertEquals(expected["bob_signed_pre_key_pub_hex"], hex(bobSPKPub))
        assertEquals(expected["bob_one_time_pre_key_pub_hex"], hex(bobOPKPub))
        assertEquals(expected["dh1_hex"], hex(dh1))
        assertEquals(expected["dh2_hex"], hex(dh2))
        assertEquals(expected["dh3_hex"], hex(dh3))
        assertEquals(expected["dh4_hex"], hex(dh4))
        assertEquals(expected["shared_secret_hex"], hex(shared))
        assertEquals(expected["root_key_hex"], hex(rootKey))
        assertEquals(expected["initiator_send_chain_key_hex"], hex(sendChain))
        assertEquals(expected["initiator_recv_chain_key_hex"], hex(recvChain))
    }

    @Test
    fun `signal fixture ratchet_step_basic`() {
        val (inputs, expected) = loadFixturePair("ratchet_step_basic")
        val chainKey = unhex(inputs["chain_key_hex"]!!)
        assertEquals(expected["message_key_hex"], hex(hmacOne(chainKey, 0x01)))
        assertEquals(expected["next_chain_key_hex"], hex(hmacOne(chainKey, 0x02)))
    }

    @Test
    fun `signal fixture ratchet_step_three_iterations`() {
        val (inputs, expected) = loadFixturePair("ratchet_step_three_iterations")
        var chainKey = unhex(inputs["initial_chain_key_hex"]!!)
        for (i in 0 until 3) {
            val msg = hmacOne(chainKey, 0x01)
            val nxt = hmacOne(chainKey, 0x02)
            assertEquals(expected["step_${i}_message_key_hex"], hex(msg))
            assertEquals(expected["step_${i}_chain_key_after_hex"], hex(nxt))
            chainKey = nxt
        }
    }

    // ─── End-to-end exercises ──────────────────────────────────────────────

    @Test
    fun `X3DH first message round-trips`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val encrypted = alice.encrypt("bob", "the mesh is alive".toByteArray())
        assertEquals(SignalProtocol.MESSAGE_TYPE_PRE_KEY, encrypted.messageType)
        assertEquals(32, encrypted.initiatorIdentityKeyX25519?.size)
        assertEquals(32, encrypted.initiatorEphemeralKeyX25519?.size)
        assertEquals("alice", encrypted.senderUhid)

        val plaintext = bob.decrypt("alice", encrypted)
        assertEquals("the mesh is alive", String(plaintext))
        assertTrue(bob.hasSession("alice"))
    }

    @Test
    fun `X3DH subsequent message is normal not pre-key`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val first = alice.encrypt("bob", "a".toByteArray())
        bob.decrypt("alice", first)

        val second = alice.encrypt("bob", "b".toByteArray())
        assertEquals(SignalProtocol.MESSAGE_TYPE_NORMAL, second.messageType)
        assertNull(second.initiatorIdentityKeyX25519)

        val out = bob.decrypt("alice", second)
        assertEquals("b", String(out))
    }

    @Test
    fun `X3DH bidirectional after first message`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val a = alice.encrypt("bob", "ping".toByteArray())
        assertEquals("ping", String(bob.decrypt("alice", a)))

        val b = bob.encrypt("alice", "pong".toByteArray())
        assertEquals(SignalProtocol.MESSAGE_TYPE_NORMAL, b.messageType)
        assertEquals("pong", String(alice.decrypt("bob", b)))
    }

    @Test
    fun `X3DH five sequential messages ratchet forward`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        for (i in 0 until 5) {
            val msg = byteArrayOf(i.toByte())
            val enc = alice.encrypt("bob", msg)
            assertEquals(i, enc.counter)
            val dec = bob.decrypt("alice", enc)
            assertContentEqualsKt(msg, dec)
        }
    }

    @Test
    fun `one-time pre-key consumed after responder establishes`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val first = alice.encrypt("bob", "first".toByteArray())
        bob.decrypt("alice", first)

        // Replay using the same bundle should fail.
        val alice2 = SignalProtocol()
        alice2.generatePreKeyBundle("alice2")
        alice2.processPreKeyBundle(bobBundle)
        val replay = alice2.encrypt("bob", "replay".toByteArray())

        assertThrows<Exception> { bob.decrypt("alice2", replay) }
    }

    @Test
    fun `encrypt without local UHID throws`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        // Note: no generatePreKeyBundle / setLocalUhid on Alice.
        alice.processPreKeyBundle(bobBundle)
        assertThrows<IllegalStateException> { alice.encrypt("bob", "x".toByteArray()) }
    }

    @Test
    fun `pre-key bundle has both Ed25519 and X25519 identity keys`() {
        val svc = SignalProtocol()
        val bundle = svc.generatePreKeyBundle("alice")
        assertEquals(32, bundle.identityKey.size)         // Ed25519
        assertEquals(32, bundle.identityKeyX25519.size)   // X25519
        assertNotEquals(hex(bundle.identityKey), hex(bundle.identityKeyX25519))
        assertEquals(32, bundle.signedPreKey.size)
        assertEquals(32, bundle.preKey.size)
        assertEquals(64, bundle.signedPreKeySignature.size)
    }

    // ─── Double Ratchet (Signal §5) tests ──────────────────────────────────

    @Test
    fun `DoubleRatchet every message carries sender ephemeral key`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val first = alice.encrypt("bob", "a".toByteArray())
        assertNotNull(first.senderEphemeralKeyX25519)
        assertEquals(32, first.senderEphemeralKeyX25519!!.size)

        bob.decrypt("alice", first)

        // Subsequent message also carries senderEphemeralKeyX25519 (same value
        // — Alice hasn't ratcheted because Bob hasn't responded yet).
        val second = alice.encrypt("bob", "b".toByteArray())
        assertNotNull(second.senderEphemeralKeyX25519)
        assertEquals(hex(first.senderEphemeralKeyX25519!!), hex(second.senderEphemeralKeyX25519!!))
    }

    @Test
    fun `DoubleRatchet sender ephemeral key rotates after roundtrip`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        // Alice -> Bob: Alice's first ratchet pub.
        val aliceFirst = alice.encrypt("bob", "ping".toByteArray())
        bob.decrypt("alice", aliceFirst)

        // Bob -> Alice: Bob's first ratchet pub (rotated by responder DH ratchet).
        val bobReply = bob.encrypt("alice", "pong".toByteArray())
        assertNotNull(bobReply.senderEphemeralKeyX25519)
        // Bob's ratchet pub MUST differ from Alice's (Bob rotated DHs on his
        // DH-ratchet step).
        assertNotEquals(hex(aliceFirst.senderEphemeralKeyX25519!!), hex(bobReply.senderEphemeralKeyX25519!!))

        alice.decrypt("bob", bobReply)

        // Alice -> Bob (after roundtrip): Alice rotates DHs on her own
        // DH-ratchet step (when she received Bob's reply).
        val aliceSecond = alice.encrypt("bob", "ping2".toByteArray())
        assertNotEquals(hex(aliceFirst.senderEphemeralKeyX25519!!), hex(aliceSecond.senderEphemeralKeyX25519!!))
        assertNotEquals(hex(bobReply.senderEphemeralKeyX25519!!), hex(aliceSecond.senderEphemeralKeyX25519!!))

        // Bob can still decrypt Alice's new message.
        val dec = bob.decrypt("alice", aliceSecond)
        assertEquals("ping2", String(dec))
    }

    @Test
    fun `DoubleRatchet previous chain count tracks messages per chain`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        // Alice sends 3 messages without a roundtrip.
        for (i in 0 until 3) {
            val enc = alice.encrypt("bob", "a$i".toByteArray())
            // PN is 0 because this IS Alice's first chain.
            assertEquals(0, enc.previousChainCount)
            bob.decrypt("alice", enc)
        }

        // Bob sends a reply, triggering his DH-ratchet step.
        val bobReply = bob.encrypt("alice", "hi".toByteArray())
        // Bob's PN reflects however many messages Bob sent in his previous
        // sending chain — which was 0 (Bob hadn't sent before his ratchet
        // rotated his chain).
        assertEquals(0, bobReply.previousChainCount)
        alice.decrypt("bob", bobReply)

        // Alice's next message after her DH-ratchet step. PN should be 3 —
        // that's how many messages she sent on her previous chain before
        // Bob's reply triggered her ratchet.
        val aliceNew = alice.encrypt("bob", "a3".toByteArray())
        assertEquals(3, aliceNew.previousChainCount)
    }

    @Test
    fun `DoubleRatchet out-of-order across DH-ratchet boundary still decrypts`() {
        // Alice sends 3 messages on chain 1. Bob receives only the first 2,
        // then Alice does a DH-ratchet (Bob replied) and sends a 4th on chain
        // 2. The 3rd message (from chain 1) arrives last — Bob must decrypt
        // via the skipped-keys cache keyed by (Alice's old DHs pub, counter=2).
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        val a0 = alice.encrypt("bob", "a0".toByteArray())
        val a1 = alice.encrypt("bob", "a1".toByteArray())
        val a2 = alice.encrypt("bob", "a2".toByteArray())

        // Bob receives a0, a1 only.
        assertEquals("a0", String(bob.decrypt("alice", a0)))
        assertEquals("a1", String(bob.decrypt("alice", a1)))

        // Bob replies — triggers his DH-ratchet step.
        val bReply = bob.encrypt("alice", "hi".toByteArray())
        alice.decrypt("bob", bReply)

        // Alice sends a4 on her new chain (after her DH-ratchet step).
        val a4 = alice.encrypt("bob", "a4".toByteArray())
        // Bob receives a4 — triggers his second DH-ratchet step. He must
        // skip-derive a key for Alice's old chain counter=2 because PN=3.
        assertEquals("a4", String(bob.decrypt("alice", a4)))

        // Now the missing a2 (from Alice's OLD chain) finally arrives. Bob
        // pulls the skipped key from cache.
        assertEquals("a2", String(bob.decrypt("alice", a2)))
    }

    @Test
    fun `DoubleRatchet long conversation all messages decrypt`() {
        val alice = SignalProtocol()
        val bob = SignalProtocol()
        val bobBundle = bob.generatePreKeyBundle("bob")
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)

        // 10 alternating messages — each side ratchets at every roundtrip.
        for (i in 0 until 10) {
            val aMsg = "alice $i"
            val aEnc = alice.encrypt("bob", aMsg.toByteArray())
            assertEquals(aMsg, String(bob.decrypt("alice", aEnc)))

            val bMsg = "bob $i"
            val bEnc = bob.encrypt("alice", bMsg.toByteArray())
            assertEquals(bMsg, String(alice.decrypt("bob", bEnc)))
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private fun loadFixturePair(caseName: String): Pair<Map<String, String>, Map<String, String>> {
        val root = repoRoot()
        val inputsFile = File(root, "fixtures/signal/inputs.json")
        val expectedFile = File(root, "fixtures/signal/expected/${caseName}.json")
        val inputsCases = parseCases(inputsFile.readText())
        val inputs = inputsCases.first { it["name"] == caseName }
        val expected = parseFlatJson(expectedFile.readText())
        return inputs to expected
    }

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    /** Minimal JSON parser for our fixed-shape fixture files (string -> string maps; cases array). */
    private fun parseCases(json: String): List<Map<String, String>> {
        // Slice between "cases":[ and matching closing ]
        val casesIdx = json.indexOf("\"cases\"")
        require(casesIdx >= 0) { "No cases array in inputs.json" }
        val openBracket = json.indexOf('[', casesIdx)
        var depth = 0
        var endIdx = -1
        for (i in openBracket until json.length) {
            when (json[i]) {
                '[' -> depth++
                ']' -> { depth--; if (depth == 0) { endIdx = i; break } }
            }
        }
        require(endIdx > 0) { "Unbalanced cases array" }
        val arrayBody = json.substring(openBracket + 1, endIdx)
        return splitObjects(arrayBody).map { parseFlatJson(it) }
    }

    private fun splitObjects(text: String): List<String> {
        val result = mutableListOf<String>()
        var depth = 0
        var start = -1
        for (i in text.indices) {
            when (text[i]) {
                '{' -> { if (depth == 0) start = i; depth++ }
                '}' -> { depth--; if (depth == 0 && start >= 0) { result += text.substring(start, i + 1); start = -1 } }
            }
        }
        return result
    }

    private fun parseFlatJson(json: String): Map<String, String> {
        // Parses a JSON object with string-only values. Ignores numeric / structured fields.
        val result = mutableMapOf<String, String>()
        val regex = Regex("\"([A-Za-z_0-9\\$]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
        for (match in regex.findAll(json)) {
            result[match.groupValues[1]] = match.groupValues[2]
        }
        return result
    }

    private fun unhex(s: String): ByteArray {
        require(s.length % 2 == 0) { "hex string must have even length" }
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun x25519DerivePub(priv: ByteArray): ByteArray {
        val p = X25519PrivateKeyParameters(priv, 0)
        return p.generatePublicKey().encoded
    }

    private fun x25519AgreeStandalone(priv: ByteArray, pub: ByteArray): ByteArray {
        val p = X25519PrivateKeyParameters(priv, 0)
        val q = X25519PublicKeyParameters(pub, 0)
        val agreement = X25519Agreement()
        agreement.init(p)
        val shared = ByteArray(agreement.agreementSize)
        agreement.calculateAgreement(q, shared, 0)
        return shared
    }

    private fun hkdf32Standalone(ikm: ByteArray, info: ByteArray): ByteArray {
        val salt = ByteArray(32)
        val mac1 = Mac.getInstance("HmacSHA256")
        mac1.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = mac1.doFinal(ikm)
        val mac2 = Mac.getInstance("HmacSHA256")
        mac2.init(SecretKeySpec(prk, "HmacSHA256"))
        mac2.update(info)
        mac2.update(0x01.toByte())
        return mac2.doFinal().copyOf(32)
    }

    private fun hmacOne(key: ByteArray, b: Byte): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(byteArrayOf(b))
    }

    private fun assertContentEqualsKt(expected: ByteArray, actual: ByteArray) {
        if (!expected.contentEquals(actual)) {
            kotlin.test.fail("byte arrays differ: expected ${hex(expected)}, got ${hex(actual)}")
        }
    }
}
