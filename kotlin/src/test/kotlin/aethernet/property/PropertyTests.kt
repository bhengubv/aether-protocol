// SPDX-License-Identifier: MIT
package aethernet.property

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketSerializer
import aethernet.protocol.PacketType
import aethernet.security.EncryptedPayload
import aethernet.security.SignalProtocol
import io.kotest.property.Arb
import io.kotest.property.RandomSource
import io.kotest.property.arbitrary.byte
import io.kotest.property.arbitrary.byteArray
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.next
import io.kotest.property.arbitrary.orNull
import io.kotest.property.arbitrary.string
import org.junit.jupiter.api.Test
import java.util.Base64
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * Property-based tests for the aether-protocol Kotlin implementation.
 *
 * Uses `kotest-property` ([Arb] + [Arb.next]) for input generation.
 * Mirrors the TypeScript `tests/fuzz.test.ts` (fast-check), the Python
 * `tests/test_fuzz.py` (Hypothesis), and the Go fuzz harness — the
 * deserializer parses untrusted bytes off the wire so the contract is:
 *
 *   - `serialize -> deserialize` is byte-stable for any well-formed
 *     `MeshPacket` (1000-iter property check).
 *   - `deserialize(arbitrary bytes)` either returns a packet or throws
 *     a documented exception (`IllegalArgumentException` or wrapped
 *     buffer-underflow / index-out-of-bounds). Anything else escaping
 *     = bug.
 *   - `tryDeserialize(arbitrary bytes)` NEVER throws. Returns null on
 *     malformed input; a `MeshPacket` otherwise.
 *   - `EncryptedPayload` JSON envelope round-trip is byte-identical.
 *   - End-to-end SignalProtocol encrypt/decrypt round-trips for any
 *     plaintext (the closest in-tree analog to a "SignalSession DTO via
 *     persistent store" — Kotlin has no externalised session DTO yet,
 *     but the same property holds via the live encrypt/decrypt APIs).
 *
 * Per-property iteration budget is 1000 — same as the TypeScript
 * (`numRuns: 1000`) and Python (`max_examples`) harnesses for cross-
 * language parity. The end-to-end Signal property runs 100 iterations
 * because each one drives a full X3DH session.
 */
class PropertyTests {

    private val rs: RandomSource = RandomSource.default()

    // ─── Arbitraries ───────────────────────────────────────────────────────

    private val packetTypeArb: Arb<PacketType> = Arb.element(*PacketType.values())

    /** UHIDs: ASCII-safe so the UTF-8 encoder round-trips cleanly. */
    private val uhidArb: Arb<String> = Arb.string(minSize = 0, maxSize = 64)

    /** Payloads up to 16KB — bench harness covers larger sizes. */
    private val payloadArb: Arb<ByteArray> = Arb.byteArray(Arb.int(0..16_384), Arb.byte())
    private val nonceArb: Arb<ByteArray> = Arb.byteArray(Arb.int(0..255), Arb.byte())
    private val sigArb: Arb<ByteArray> = Arb.byteArray(Arb.int(0..255), Arb.byte())
    private val arbitraryBytesArb: Arb<ByteArray> = Arb.byteArray(Arb.int(0..2048), Arb.byte())

    private fun randomUuid(): UUID {
        // 16 random bytes -> UUID. Avoids the kotest 5.8.0 omission of
        // a built-in UUID arbitrary.
        val msb = Arb.long().next(rs)
        val lsb = Arb.long().next(rs)
        return UUID(msb, lsb)
    }

    private fun randomMeshPacket(): MeshPacket = MeshPacket(
        id = randomUuid(),
        type = packetTypeArb.next(rs),
        sourceUhid = uhidArb.next(rs),
        destinationUhid = uhidArb.next(rs),
        ttl = Arb.int(0..0x7FFFFFFF).next(rs),
        priority = Arb.byte().next(rs),
        payload = payloadArb.next(rs),
        packetNonce = nonceArb.next(rs),
        signature = sigArb.next(rs),
        timestampMs = Arb.long(0L..Long.MAX_VALUE).next(rs),
        createdAt = 0L,
        protocolVersion = Arb.byte().next(rs),
    )

    // ─── PacketSerializer round-trip ───────────────────────────────────────

    @Test
    fun `PacketSerializer serialize then deserialize is byte-stable for any MeshPacket`() {
        repeat(NUM_RUNS) {
            val pkt = randomMeshPacket()
            val wire = PacketSerializer.serialize(pkt)
            val got = PacketSerializer.deserialize(wire)
            assertEquals(pkt.id, got.id, "id round-trip")
            assertEquals(pkt.type, got.type, "type round-trip")
            assertEquals(pkt.sourceUhid, got.sourceUhid, "sourceUhid round-trip")
            assertEquals(pkt.destinationUhid, got.destinationUhid, "destinationUhid round-trip")
            assertEquals(pkt.ttl, got.ttl, "ttl round-trip")
            assertEquals(pkt.priority, got.priority, "priority round-trip")
            assertContentEquals(pkt.payload, got.payload, "payload round-trip")
            assertContentEquals(pkt.packetNonce, got.packetNonce, "nonce round-trip")
            assertContentEquals(pkt.signature, got.signature, "signature round-trip")
            assertEquals(pkt.timestampMs, got.timestampMs, "timestampMs round-trip")
            assertEquals(pkt.protocolVersion, got.protocolVersion, "protocolVersion round-trip")
        }
    }

    // ─── PacketSerializer.deserialize(arbitrary bytes) ─────────────────────

    /**
     * Documented exception set for `PacketSerializer.deserialize`. Anything
     * outside this set escaping = bug.
     *
     *   - `IllegalArgumentException`: domain wire-format failures (length
     *     prefix, unknown packet type, insufficient remaining).
     *   - `java.nio.BufferUnderflowException`: truncated header below the
     *     fixed-prefix threshold — the deserializer guards `data.size < 43`
     *     but a buffer underflow can still surface from internal calls on
     *     edge inputs.
     *   - `IndexOutOfBoundsException`: truncated UTF-8 frame.
     */
    private fun isDocumentedDeserializeError(t: Throwable): Boolean = when (t) {
        is IllegalArgumentException -> true
        is java.nio.BufferUnderflowException -> true
        is IndexOutOfBoundsException -> true
        else -> false
    }

    @Test
    fun `PacketSerializer deserialize never throws an undocumented exception on arbitrary bytes`() {
        repeat(NUM_RUNS) {
            val data = arbitraryBytesArb.next(rs)
            try {
                val pkt = PacketSerializer.deserialize(data)
                // Success path — must not silently return a malformed packet.
                assertNotNull(pkt)
                assertNotNull(pkt.id)
            } catch (t: Throwable) {
                if (!isDocumentedDeserializeError(t)) {
                    fail("Undocumented exception on input ${data.size} bytes: ${t::class.qualifiedName}: ${t.message}")
                }
            }
        }
    }

    @Test
    fun `PacketSerializer tryDeserialize never throws on arbitrary bytes`() {
        repeat(NUM_RUNS) {
            val data = arbitraryBytesArb.next(rs)
            // Must not throw under any circumstance.
            val result = PacketSerializer.tryDeserialize(data)
            // Result is null or a well-formed MeshPacket.
            if (result != null) {
                assertNotNull(result.id)
            }
        }
    }

    @Test
    fun `PacketSerializer rejects negative payload length`() {
        // Hand-built header with payload-length = -1. Mirrors TS / Python / Go.
        // Layout: [0]=ver [1]=type [2..17]=uuid [18]=priority [19..22]=ttl
        // [23..30]=ts [31..32]=srcLen [33..34]=dstLen [35..36]=nonceLen
        // [37..40]=payloadLen [41..42]=sigLen — all little-endian.
        val buf = ByteArray(43)
        buf[0] = 0x02
        buf[1] = 0x03
        buf[18] = 0x05
        buf[19] = 0x07
        buf[37] = 0xFF.toByte()
        buf[38] = 0xFF.toByte()
        buf[39] = 0xFF.toByte()
        buf[40] = 0xFF.toByte()
        try {
            PacketSerializer.deserialize(buf)
            fail("Expected IllegalArgumentException for negative payload length.")
        } catch (e: IllegalArgumentException) {
            // expected
        }
    }

    @Test
    fun `PacketSerializer rejects oversize payload length without allocating`() {
        // payloadLen = 0x7FFFFFFF — more bytes than the buffer holds.
        // Must throw without allocating ~2 GiB.
        val oversizes = intArrayOf(0x7FFFFFFF, 0x10000000, 0x01000000)
        for (oversize in oversizes) {
            val buf = ByteArray(43)
            buf[0] = 0x02
            buf[1] = 0x03
            buf[18] = 0x05
            buf[19] = 0x07
            buf[37] = (oversize and 0xFF).toByte()
            buf[38] = ((oversize ushr 8) and 0xFF).toByte()
            buf[39] = ((oversize ushr 16) and 0xFF).toByte()
            buf[40] = ((oversize ushr 24) and 0xFF).toByte()
            try {
                PacketSerializer.deserialize(buf)
                fail("Expected exception for oversize payload length 0x${oversize.toString(16)}")
            } catch (e: Throwable) {
                assertTrue(
                    isDocumentedDeserializeError(e),
                    "Expected documented exception for oversize input, got ${e::class.qualifiedName}",
                )
            }
        }
    }

    // ─── Mutation fuzzer over a valid wire envelope ─────────────────────────

    @Test
    fun `PacketSerializer deserialize never throws an undocumented exception on bit-flipped wire bytes`() {
        repeat(NUM_RUNS) {
            val pkt = randomMeshPacket()
            val valid = PacketSerializer.serialize(pkt)
            if (valid.isEmpty()) return@repeat

            val mutated = valid.copyOf()
            val mutationCount = Arb.int(1..4).next(rs)
            for (i in 0 until mutationCount) {
                val pos = Arb.int(0 until mutated.size).next(rs)
                mutated[pos] = (mutated[pos].toInt() xor 0xA5).toByte()
            }

            try {
                PacketSerializer.deserialize(mutated)
            } catch (t: Throwable) {
                if (!isDocumentedDeserializeError(t)) {
                    fail("Undocumented exception on mutated wire: ${t::class.qualifiedName}: ${t.message}")
                }
            }
        }
    }

    // ─── EncryptedPayload JSON codec round-trip ─────────────────────────────

    /**
     * Inline JSON codec for `EncryptedPayload`. A host wiring the protocol
     * over a JSON-friendly transport (REST, WebSocket, IndexedDB) needs one,
     * but the protocol layer itself only deals with the in-memory shape.
     * The codec is exercised here as a property target: ANY EncryptedPayload
     * round-trips with byte-identical fields.
     */
    private fun encodeEncryptedPayload(p: EncryptedPayload): String {
        val sb = StringBuilder("{")
        sb.append("\"ciphertext\":\"").append(b64(p.ciphertext)).append("\",")
        sb.append("\"nonce\":\"").append(b64(p.nonce)).append("\",")
        sb.append("\"messageType\":").append(p.messageType).append(",")
        sb.append("\"senderUhid\":\"").append(jsonEscape(p.senderUhid)).append("\",")
        sb.append("\"counter\":").append(p.counter).append(",")
        sb.append("\"usedSignedPreKeyId\":").append(p.usedSignedPreKeyId).append(",")
        sb.append("\"usedOneTimePreKeyId\":").append(p.usedOneTimePreKeyId).append(",")
        sb.append("\"previousChainCount\":").append(p.previousChainCount)
        if (p.initiatorIdentityKeyX25519 != null) {
            sb.append(",\"initiatorIdentityKeyX25519\":\"").append(b64(p.initiatorIdentityKeyX25519!!)).append("\"")
        }
        if (p.initiatorEphemeralKeyX25519 != null) {
            sb.append(",\"initiatorEphemeralKeyX25519\":\"").append(b64(p.initiatorEphemeralKeyX25519!!)).append("\"")
        }
        if (p.senderEphemeralKeyX25519 != null) {
            sb.append(",\"senderEphemeralKeyX25519\":\"").append(b64(p.senderEphemeralKeyX25519!!)).append("\"")
        }
        sb.append("}")
        return sb.toString()
    }

    private fun decodeEncryptedPayload(json: String): EncryptedPayload {
        val ct = b64Decode(extractString(json, "ciphertext"))
        val n = b64Decode(extractString(json, "nonce"))
        val mt = extractInt(json, "messageType")
        val sender = extractString(json, "senderUhid")
        val counter = extractInt(json, "counter")
        val spkId = extractInt(json, "usedSignedPreKeyId")
        val opkId = extractInt(json, "usedOneTimePreKeyId")
        val pcc = extractInt(json, "previousChainCount")
        val iik = extractStringOrNull(json, "initiatorIdentityKeyX25519")?.let { b64Decode(it) }
        val iek = extractStringOrNull(json, "initiatorEphemeralKeyX25519")?.let { b64Decode(it) }
        val sek = extractStringOrNull(json, "senderEphemeralKeyX25519")?.let { b64Decode(it) }
        return EncryptedPayload(
            ciphertext = ct,
            nonce = n,
            messageType = mt,
            senderUhid = sender,
            counter = counter,
            initiatorIdentityKeyX25519 = iik,
            initiatorEphemeralKeyX25519 = iek,
            usedSignedPreKeyId = spkId,
            usedOneTimePreKeyId = opkId,
            senderEphemeralKeyX25519 = sek,
            previousChainCount = pcc,
        )
    }

    @Test
    fun `EncryptedPayload JSON codec round-trips for any payload`() {
        // ASCII-only sender ids so the JSON escape path is straightforward.
        // Unicode escapes are exercised in PacketSerializerTest via the
        // wire-format round-trip; the JSON envelope is its own concern.
        val asciiSender: Arb<String> = Arb.string(minSize = 0, maxSize = 32)
        repeat(NUM_RUNS) {
            val payload = EncryptedPayload(
                ciphertext = Arb.byteArray(Arb.int(0..1024), Arb.byte()).next(rs),
                nonce = Arb.byteArray(Arb.int(12..12), Arb.byte()).next(rs),
                messageType = Arb.element(
                    SignalProtocol.MESSAGE_TYPE_NORMAL,
                    SignalProtocol.MESSAGE_TYPE_PRE_KEY,
                ).next(rs),
                senderUhid = asciiSender.next(rs),
                counter = Arb.int(0..0x7FFFFFFF).next(rs),
                initiatorIdentityKeyX25519 = Arb.byteArray(Arb.int(32..32), Arb.byte()).orNull().next(rs),
                initiatorEphemeralKeyX25519 = Arb.byteArray(Arb.int(32..32), Arb.byte()).orNull().next(rs),
                usedSignedPreKeyId = Arb.int(0..0x7FFFFFFF).next(rs),
                usedOneTimePreKeyId = Arb.int(0..0x7FFFFFFF).next(rs),
                senderEphemeralKeyX25519 = Arb.byteArray(Arb.int(32..32), Arb.byte()).orNull().next(rs),
                previousChainCount = Arb.int(0..0x7FFFFFFF).next(rs),
            )
            val json = encodeEncryptedPayload(payload)
            val got = decodeEncryptedPayload(json)
            assertContentEquals(payload.ciphertext, got.ciphertext, "ciphertext")
            assertContentEquals(payload.nonce, got.nonce, "nonce")
            assertEquals(payload.messageType, got.messageType, "messageType")
            assertEquals(payload.senderUhid, got.senderUhid, "senderUhid")
            assertEquals(payload.counter, got.counter, "counter")
            assertEquals(payload.usedSignedPreKeyId, got.usedSignedPreKeyId, "usedSignedPreKeyId")
            assertEquals(payload.usedOneTimePreKeyId, got.usedOneTimePreKeyId, "usedOneTimePreKeyId")
            assertEquals(payload.previousChainCount, got.previousChainCount, "previousChainCount")
            if (payload.initiatorIdentityKeyX25519 == null) {
                assertNull(got.initiatorIdentityKeyX25519)
            } else {
                assertContentEquals(payload.initiatorIdentityKeyX25519!!, got.initiatorIdentityKeyX25519!!)
            }
            if (payload.initiatorEphemeralKeyX25519 == null) {
                assertNull(got.initiatorEphemeralKeyX25519)
            } else {
                assertContentEquals(payload.initiatorEphemeralKeyX25519!!, got.initiatorEphemeralKeyX25519!!)
            }
            if (payload.senderEphemeralKeyX25519 == null) {
                assertNull(got.senderEphemeralKeyX25519)
            } else {
                assertContentEquals(payload.senderEphemeralKeyX25519!!, got.senderEphemeralKeyX25519!!)
            }
        }
    }

    // ─── End-to-end SignalProtocol session round-trip ──────────────────────

    /**
     * Closest in-tree analog to "SignalSession DTO via persistent store"
     * for Kotlin. SignalSession itself is `internal` (no externalised DTO
     * yet — the TS / Python `KeyValueSignalSessionStore` shape is not
     * mirrored here), so the property is checked at the API boundary:
     * for any plaintext, `bob.decrypt(alice.encrypt(p)) == p`. A future
     * change that broke the in-memory ratchet shape would surface here
     * the same way it would in the TS DTO codec.
     *
     * Iteration count: 100 (each iteration runs an X3DH session — heavier
     * than the pure-codec properties at 1000).
     */
    @Test
    fun `SignalProtocol encrypt then decrypt round-trips for any plaintext`() {
        repeat(SIGNAL_NUM_RUNS) {
            val plaintext = Arb.byteArray(Arb.int(0..1024), Arb.byte()).next(rs)
            val alice = SignalProtocol()
            val bob = SignalProtocol()
            val bobBundle = bob.generatePreKeyBundle("bob")
            alice.generatePreKeyBundle("alice")
            alice.processPreKeyBundle(bobBundle)
            val enc = alice.encrypt("bob", plaintext)
            val dec = bob.decrypt("alice", enc)
            assertContentEquals(plaintext, dec, "plaintext round-trip")
        }
    }

    // ─── Tiny JSON helpers ─────────────────────────────────────────────────

    private fun b64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)
    private fun b64Decode(s: String): ByteArray = Base64.getDecoder().decode(s)

    private fun jsonEscape(s: String): String {
        val sb = StringBuilder(s.length + 2)
        for (ch in s) {
            when (ch) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> if (ch.code < 0x20) {
                    sb.append("\\u%04x".format(ch.code))
                } else {
                    sb.append(ch)
                }
            }
        }
        return sb.toString()
    }

    private fun extractString(json: String, key: String): String =
        extractStringOrNull(json, key) ?: error("missing key $key in JSON")

    private fun extractStringOrNull(json: String, key: String): String? {
        val pat = Regex("\"$key\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
        return pat.find(json)?.groupValues?.get(1)?.let { decodeJsonStringEscapes(it) }
    }

    private fun extractInt(json: String, key: String): Int {
        val pat = Regex("\"$key\"\\s*:\\s*(-?\\d+)")
        val m = pat.find(json) ?: error("missing key $key in JSON")
        return m.groupValues[1].toInt()
    }

    private fun decodeJsonStringEscapes(raw: String): String {
        val sb = StringBuilder(raw.length)
        var i = 0
        while (i < raw.length) {
            val ch = raw[i]
            if (ch == '\\' && i + 1 < raw.length) {
                when (val next = raw[i + 1]) {
                    '\\' -> sb.append('\\')
                    '"' -> sb.append('"')
                    'n' -> sb.append('\n')
                    'r' -> sb.append('\r')
                    't' -> sb.append('\t')
                    'u' -> {
                        if (i + 5 < raw.length) {
                            sb.append(raw.substring(i + 2, i + 6).toInt(16).toChar())
                            i += 4
                        } else {
                            sb.append(next)
                        }
                    }
                    else -> sb.append(next)
                }
                i += 2
            } else {
                sb.append(ch)
                i++
            }
        }
        return sb.toString()
    }

    companion object {
        /** Per-property iteration budget — matches TS `numRuns: 1000`. */
        const val NUM_RUNS = 1000

        /** Per-property iteration budget for the heavy Signal property (X3DH per iter). */
        const val SIGNAL_NUM_RUNS = 100
    }
}
