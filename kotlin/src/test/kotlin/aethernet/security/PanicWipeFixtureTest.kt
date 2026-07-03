// SPDX-License-Identifier: MIT
package aethernet.security

import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Cross-language panic-wipe fixture verifier.
 *
 * Verifies that the Kotlin implementation reproduces fixtures/panicwipe/vectors.json
 * byte-for-byte — the same duress-PIN SHA-256 hashes, the canonical identity
 * key-store names, and the pre-key naming patterns that the C# reference and every
 * AetherNet language SDK must produce. Any drift surfaces here as a hex / name
 * mismatch. SecureErase is behavioural and is checked directly.
 */
class PanicWipeFixtureTest {

    // ─── Fixture parity gate ────────────────────────────────────────────────

    @Test
    fun `duress pin hashes match byte-for-byte and verify`() {
        val fx = loadFixture()
        assertTrue(fx.duressPinHashes.isNotEmpty(), "no duress_pin_hashes in fixture")
        for (v in fx.duressPinHashes) {
            val hash = PanicWipe.duressPinHash(v.pin)
            assertEquals(v.sha256, hex(hash), "duressPinHash mismatch for pin='${v.pin}'")
            assertTrue(
                PanicWipe.verifyDuressPin(v.pin, hash),
                "correct PIN must verify against its own hash (pin='${v.pin}')",
            )
            assertFalse(
                PanicWipe.verifyDuressPin(v.pin + "x", hash),
                "wrong PIN must not verify (pin='${v.pin}')",
            )
        }
    }

    @Test
    fun `identity key names match the fixture`() {
        val fx = loadFixture()
        assertContentEquals(fx.identityKeyNames, PanicWipe.IDENTITY_KEY_NAMES)
    }

    @Test
    fun `max prekeys matches the fixture`() {
        val fx = loadFixture()
        assertEquals(fx.maxPreKeys, PanicWipe.MAX_PRE_KEYS)
    }

    @Test
    fun `prekey and signed-prekey names match the fixture`() {
        val fx = loadFixture()
        assertEquals(fx.preKeyExpected, PanicWipe.preKeyName(fx.preKeyIndex))
        assertEquals(fx.signedPreKeyExpected, PanicWipe.signedPreKeyName(fx.signedPreKeyIndex))
    }

    // ─── SecureErase + reject paths ─────────────────────────────────────────

    @Test
    fun `secureErase zeroes a buffer`() {
        val buf = ByteArray(64) { (it + 1).toByte() }
        PanicWipe.secureErase(buf)
        assertTrue(buf.all { it == 0.toByte() }, "secureErase must leave the buffer zeroed")
    }

    @Test
    fun `verifyDuressPin rejects a 16-byte hash`() {
        assertFalse(PanicWipe.verifyDuressPin("0000", ByteArray(16)))
    }

    // ─── Fixture model + loader (mirrors BlePrivacyFixtureTest) ──────────────

    private data class PinVector(val pin: String, val sha256: String)

    private data class Fixture(
        val maxPreKeys: Int,
        val identityKeyNames: List<String>,
        val preKeyIndex: Int,
        val preKeyExpected: String,
        val signedPreKeyIndex: Int,
        val signedPreKeyExpected: String,
        val duressPinHashes: List<PinVector>,
    )

    private fun loadFixture(): Fixture {
        val json = File(repoRoot(), "fixtures/panicwipe/vectors.json").readText()
        return Fixture(
            maxPreKeys = scalarInt(json, "max_prekeys"),
            identityKeyNames = parseStringArray(json, "identity_key_names"),
            preKeyIndex = objectInt(json, "prekey_name", "index"),
            preKeyExpected = objectString(json, "prekey_name", "expected"),
            signedPreKeyIndex = objectInt(json, "signed_prekey_name", "index"),
            signedPreKeyExpected = objectString(json, "signed_prekey_name", "expected"),
            duressPinHashes = parsePinVectors(json),
        )
    }

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun scalarInt(json: String, name: String): Int =
        Regex("\"$name\"\\s*:\\s*(-?\\d+)").find(json)!!.groupValues[1].toInt()

    /** A scalar string field nested inside a named object, e.g. prekey_name.expected. */
    private fun objectString(json: String, objectName: String, field: String): String {
        val obj = objectBody(json, objectName)
        return Regex("\"$field\"\\s*:\\s*\"([^\"]*)\"").find(obj)!!.groupValues[1]
    }

    /** A scalar int field nested inside a named object, e.g. prekey_name.index. */
    private fun objectInt(json: String, objectName: String, field: String): Int {
        val obj = objectBody(json, objectName)
        return Regex("\"$field\"\\s*:\\s*(-?\\d+)").find(obj)!!.groupValues[1].toInt()
    }

    /** Extracts the `{ ... }` body of a named object via brace-depth scanning. */
    private fun objectBody(json: String, objectName: String): String {
        val nameIdx = json.indexOf("\"$objectName\"")
        require(nameIdx >= 0) { "No '$objectName' object in vectors.json" }
        val openBrace = json.indexOf('{', nameIdx)
        var depth = 0
        var endIdx = -1
        for (i in openBrace until json.length) {
            when (json[i]) {
                '{' -> depth++
                '}' -> { depth--; if (depth == 0) { endIdx = i; break } }
            }
        }
        require(endIdx > 0) { "Unbalanced '$objectName' object" }
        return json.substring(openBrace, endIdx + 1)
    }

    /** Parses a named array of bare strings, e.g. identity_key_names. */
    private fun parseStringArray(json: String, arrayName: String): List<String> =
        Regex("\"([^\"]*)\"").findAll(arrayBody(json, arrayName)).map { it.groupValues[1] }.toList()

    /** Parses the duress_pin_hashes array of `{ "pin": ..., "sha256": ... }`. */
    private fun parsePinVectors(json: String): List<PinVector> =
        splitObjects(arrayBody(json, "duress_pin_hashes")).map { obj ->
            val pin = Regex("\"pin\"\\s*:\\s*\"([^\"]*)\"").find(obj)!!.groupValues[1]
            val sha256 = Regex("\"sha256\"\\s*:\\s*\"([^\"]*)\"").find(obj)!!.groupValues[1]
            PinVector(pin, sha256)
        }

    /** Extracts the `[ ... ]` body of a named array via bracket-depth scanning. */
    private fun arrayBody(json: String, arrayName: String): String {
        val nameIdx = json.indexOf("\"$arrayName\"")
        require(nameIdx >= 0) { "No '$arrayName' array in vectors.json" }
        val openBracket = json.indexOf('[', nameIdx)
        var depth = 0
        var endIdx = -1
        for (i in openBracket until json.length) {
            when (json[i]) {
                '[' -> depth++
                ']' -> { depth--; if (depth == 0) { endIdx = i; break } }
            }
        }
        require(endIdx > 0) { "Unbalanced '$arrayName' array" }
        return json.substring(openBracket + 1, endIdx)
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

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
}
