// SPDX-License-Identifier: MIT
package aethernet.security

import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.File
import java.security.SecureRandom
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Cross-language BIP-39 fixture verifier and end-to-end recovery-phrase
 * exercises.
 *
 * Verifies that the Kotlin implementation reproduces the official Trezor BIP-39
 * test vectors (fixtures/bip39/vectors.json) byte-for-byte — the same three
 * columns (entropy -> mnemonic -> seed) that every AetherNet language SDK and
 * the C# reference must reproduce. Any drift surfaces here as a hex mismatch.
 */
class Bip39FixtureTest {

    // ─── Official Trezor vectors (the parity gate) ──────────────────────────

    @Test
    fun `bip39 official trezor vectors round-trip byte-for-byte`() {
        val (passphrase, vectors) = loadVectors()
        assertEquals("TREZOR", passphrase)
        assertEquals(24, vectors.size)

        for (v in vectors) {
            val entropyHex = v["entropy"]!!
            val mnemonic = v["mnemonic"]!!
            val seedHex = v["seed"]!!

            // entropy -> mnemonic
            assertEquals(mnemonic, Bip39.entropyToMnemonic(unhex(entropyHex)),
                "entropyToMnemonic mismatch for $entropyHex")

            // mnemonic -> entropy (checksum enforced)
            assertEquals(entropyHex, hex(Bip39.mnemonicToEntropy(mnemonic)),
                "mnemonicToEntropy mismatch for '$mnemonic'")

            // mnemonic -> 64-byte seed (PBKDF2-HMAC-SHA512, passphrase TREZOR)
            assertEquals(seedHex, hex(Bip39.mnemonicToSeed(mnemonic, passphrase)),
                "mnemonicToSeed mismatch for '$mnemonic'")
        }
    }

    // ─── (a) Identity backup: known 24-word vector ──────────────────────────

    @Test
    fun `identity recovery phrase matches known vector and restores private key`() {
        val entropyHex = "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f"
        val expectedPhrase =
            "void come effort suffer camp survey warrior heavy shoot primary clutch crush " +
            "open amazing screen patrol group space point ten exist slush involve unfold"

        val seed = unhex(entropyHex)
        assertEquals(expectedPhrase, Bip39.toRecoveryPhrase(seed))

        val (restoredPrivate, _) = Bip39.fromRecoveryPhrase(expectedPhrase)
        assertEquals(entropyHex, hex(restoredPrivate))
    }

    // ─── (b) Random identity: backup -> restore -> sign/verify ──────────────

    @Test
    fun `random identity survives backup and restore with working signature`() {
        val rng = SecureRandom()
        val seed = ByteArray(32).also { rng.nextBytes(it) }
        // Independent oracle for the expected public key (BouncyCastle directly).
        val expectedPublic = Ed25519PrivateKeyParameters(seed, 0).generatePublicKey().encoded

        val phrase = Bip39.toRecoveryPhrase(seed)
        assertEquals(24, phrase.split(' ').size)
        assertTrue(Bip39.isValid(phrase))

        val (restoredPrivate, restoredPublic) = Bip39.fromRecoveryPhrase(phrase)
        assertContentEquals(seed, restoredPrivate, "restored private key must match")
        assertContentEquals(expectedPublic, restoredPublic, "restored public key must match")

        // The restored key is a live Ed25519 identity: it signs and verifies.
        val message = "the mesh is alive".toByteArray()
        val signature = Ed25519Service.sign(restoredPrivate, message)
        assertTrue(Ed25519Service.verify(restoredPublic, message, signature),
            "restored identity must produce a verifiable signature")
    }

    // ─── (c) Reject paths: a mistyped phrase must throw, never silently wrong ─

    @Test
    fun `bad checksum phrase is rejected`() {
        // 24 x "abandon" is well-formed structurally but has an invalid checksum.
        val badChecksum = (0 until 24).joinToString(" ") { "abandon" }
        assertThrows<IllegalArgumentException> { Bip39.mnemonicToEntropy(badChecksum) }
        assertThrows<IllegalArgumentException> { Bip39.fromRecoveryPhrase(badChecksum) }
        assertFalse(Bip39.isValid(badChecksum))
    }

    @Test
    fun `unknown word is rejected`() {
        // Valid 24-word phrase with one word swapped for a non-wordlist token.
        val valid =
            "void come effort suffer camp survey warrior heavy shoot primary clutch crush " +
            "open amazing screen patrol group space point ten exist slush involve unfold"
        val words = valid.split(' ').toMutableList()
        words[5] = "notaword"
        val bad = words.joinToString(" ")
        assertThrows<IllegalArgumentException> { Bip39.mnemonicToEntropy(bad) }
        assertFalse(Bip39.isValid(bad))
    }

    @Test
    fun `wrong word count is rejected`() {
        val threeWords = "abandon ability able"
        assertThrows<IllegalArgumentException> { Bip39.mnemonicToEntropy(threeWords) }
        assertThrows<IllegalArgumentException> { Bip39.fromRecoveryPhrase(threeWords) }
        assertFalse(Bip39.isValid(threeWords))
    }

    @Test
    fun `non-32-byte entropy is rejected for identity backup`() {
        // A valid 12-word phrase decodes to 16 bytes — not a 256-bit identity seed.
        val twelveWord =
            "abandon abandon abandon abandon abandon abandon " +
            "abandon abandon abandon abandon abandon about"
        assertTrue(Bip39.isValid(twelveWord)) // valid BIP-39, just not an identity
        assertThrows<IllegalArgumentException> { Bip39.fromRecoveryPhrase(twelveWord) }

        assertThrows<IllegalArgumentException> { Bip39.toRecoveryPhrase(ByteArray(16)) }
    }

    // ─── Wordlist integrity ─────────────────────────────────────────────────

    @Test
    fun `embedded wordlist has 2048 words matching the fixture`() {
        val fixtureWords = File(repoRoot(), "fixtures/bip39/english.txt")
            .readLines()
            .filter { it.isNotBlank() }
        assertEquals(2048, Bip39.words.size)
        assertEquals(fixtureWords, Bip39.words, "embedded wordlist must equal fixtures/bip39/english.txt")
    }

    // ─── Helpers (mirrors SignalFixtureTest) ────────────────────────────────

    private fun loadVectors(): Pair<String, List<Map<String, String>>> {
        val json = File(repoRoot(), "fixtures/bip39/vectors.json").readText()
        val passphrase = Regex("\"passphrase\"\\s*:\\s*\"([^\"]*)\"")
            .find(json)!!.groupValues[1]
        val vectors = parseArray(json, "vectors")
        return passphrase to vectors
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

    /** Minimal JSON parser for our fixed-shape fixture file (named array of string->string objects). */
    private fun parseArray(json: String, arrayName: String): List<Map<String, String>> {
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
}
