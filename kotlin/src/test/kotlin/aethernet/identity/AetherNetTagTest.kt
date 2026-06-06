// SPDX-License-Identifier: MIT

package aethernet.identity

import org.junit.jupiter.api.Test
import kotlin.test.*

/**
 * Tests for [AetherNetTag] — the human-readable identity address primitive.
 *
 * The implementation:
 *   SHA-256(publicKey) → first 50 bits → 10 Crockford base-32 chars → "XXXXX-XXXXX"
 */
class AetherNetTagTest {

    // ── Known-vector ──────────────────────────────────────────────────────────

    @Test fun `fromPublicKey produces XXXXX-XXXXX format`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        val value = tag.value
        assertEquals(11, value.length, "tag should be 11 characters including dash")
        assertEquals('-', value[5], "character at index 5 should be a dash")
    }

    @Test fun `fromPublicKey output contains only Crockford base-32 chars and dash`() {
        val validChars = setOf(
            '0','1','2','3','4','5','6','7','8','9',
            'A','B','C','D','E','F','G','H','J','K',
            'M','N','P','Q','R','S','T','V','W','X','Y','Z',
            '-'
        )
        val key = ByteArray(32) { (it * 7 + 3).toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        for (ch in tag.value) {
            assertTrue(ch in validChars, "unexpected character '$ch' in tag '${tag.value}'")
        }
    }

    @Test fun `fromPublicKey for all-zeros key produces consistent output`() {
        val key = ByteArray(32) { 0 }
        val tag1 = AetherNetTag.fromPublicKey(key)
        val tag2 = AetherNetTag.fromPublicKey(key)
        assertEquals(tag1, tag2)
    }

    @Test fun `fromPublicKey for all-0xFF key produces consistent output`() {
        val key = ByteArray(32) { 0xFF.toByte() }
        val tag1 = AetherNetTag.fromPublicKey(key)
        val tag2 = AetherNetTag.fromPublicKey(key)
        assertEquals(tag1, tag2)
    }

    // ── Same key → same tag (determinism) ─────────────────────────────────────

    @Test fun `same key always produces the same tag`() {
        val key = ByteArray(32) { (it * 13).toByte() }
        val tags = (1..10).map { AetherNetTag.fromPublicKey(key.copyOf()) }
        assertTrue(tags.all { it == tags[0] }, "all tags for same key must match")
    }

    // ── Different keys → different tags ───────────────────────────────────────

    @Test fun `different keys produce different tags`() {
        val key1 = ByteArray(32) { it.toByte() }
        val key2 = ByteArray(32) { (it + 1).toByte() }
        assertNotEquals(AetherNetTag.fromPublicKey(key1), AetherNetTag.fromPublicKey(key2))
    }

    @Test fun `keys differing only in last byte produce different tags`() {
        val key1 = ByteArray(32) { 0xAA.toByte() }
        val key2 = key1.copyOf().also { it[31] = 0x55.toByte() }
        assertNotEquals(AetherNetTag.fromPublicKey(key1), AetherNetTag.fromPublicKey(key2))
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    @Test fun `fromPublicKey → toString → parse round-trips`() {
        val key = ByteArray(32) { (it * 3 + 7).toByte() }
        val original = AetherNetTag.fromPublicKey(key)
        val parsed = AetherNetTag.parse(original.toString())
        assertEquals(original, parsed)
    }

    @Test fun `round-trip for multiple keys`() {
        for (seed in 0..9) {
            val key = ByteArray(32) { (it + seed * 17).toByte() }
            val original = AetherNetTag.fromPublicKey(key)
            val parsed = AetherNetTag.parse(original.toString())
            assertEquals(original, parsed, "round-trip failed for seed $seed")
        }
    }

    // ── verify() ──────────────────────────────────────────────────────────────

    @Test fun `verify returns true when tag matches key`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key).value
        assertTrue(AetherNetTag.verify(tag, key))
    }

    @Test fun `verify returns false when key is wrong`() {
        val key1 = ByteArray(32) { it.toByte() }
        val key2 = ByteArray(32) { (it + 50).toByte() }
        val tag = AetherNetTag.fromPublicKey(key1).value
        assertFalse(AetherNetTag.verify(tag, key2))
    }

    @Test fun `verify returns false for invalid tag string`() {
        val key = ByteArray(32) { it.toByte() }
        assertFalse(AetherNetTag.verify("not-a-tag", key))
        assertFalse(AetherNetTag.verify("", key))
    }

    @Test fun `verify returns false for key with wrong length`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key).value
        assertFalse(AetherNetTag.verify(tag, ByteArray(16)))
    }

    // ── parse() — accepted formats ─────────────────────────────────────────────

    @Test fun `parse accepts canonical XXXXX-XXXXX format`() {
        val key = ByteArray(32) { it.toByte() }
        val canonical = AetherNetTag.fromPublicKey(key).value
        val parsed = AetherNetTag.parse(canonical)
        assertEquals(canonical, parsed.value)
    }

    @Test fun `parse accepts 10-char input without separator`() {
        val key = ByteArray(32) { it.toByte() }
        val canonical = AetherNetTag.fromPublicKey(key).value
        val noSep = canonical.replace("-", "")
        assertEquals(10, noSep.length)
        val parsed = AetherNetTag.parse(noSep)
        assertEquals(canonical, parsed.value)
    }

    @Test fun `parse accepts lowercase input`() {
        val key = ByteArray(32) { it.toByte() }
        val canonical = AetherNetTag.fromPublicKey(key).value
        val lower = canonical.lowercase()
        val parsed = AetherNetTag.parse(lower)
        assertEquals(canonical, parsed.value)
    }

    @Test fun `parse accepts mixed-case input`() {
        val key = ByteArray(32) { it.toByte() }
        val canonical = AetherNetTag.fromPublicKey(key).value
        // alternate upper/lower
        val mixed = canonical.mapIndexed { i, c -> if (i % 2 == 0) c.uppercaseChar() else c.lowercaseChar() }.joinToString("")
        val parsed = AetherNetTag.parse(mixed)
        assertEquals(canonical, parsed.value)
    }

    // ── parse() — rejected inputs ─────────────────────────────────────────────

    @Test fun `parse throws for empty string`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("")
        }
    }

    @Test fun `parse throws for wrong total length — too short`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("ABCD-ABCD")  // 9 chars
        }
    }

    @Test fun `parse throws for wrong total length — too long`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("ABCDE-ABCDEF")  // 12 chars
        }
    }

    @Test fun `parse throws for invalid character I`() {
        // 'I' is excluded from Crockford alphabet
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("IIIII-IIIII")
        }
    }

    @Test fun `parse throws for invalid character L`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("LLLLL-LLLLL")
        }
    }

    @Test fun `parse throws for invalid character O`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("OOOOO-OOOOO")
        }
    }

    @Test fun `parse throws for invalid character U`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("UUUUU-UUUUU")
        }
    }

    @Test fun `parse throws for special characters`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.parse("!@#$%-^&*()")
        }
    }

    // ── tryParse() ────────────────────────────────────────────────────────────

    @Test fun `tryParse returns AetherNetTag for valid input`() {
        val key = ByteArray(32) { it.toByte() }
        val canonical = AetherNetTag.fromPublicKey(key).value
        assertNotNull(AetherNetTag.tryParse(canonical))
    }

    @Test fun `tryParse returns null for invalid input`() {
        assertNull(AetherNetTag.tryParse(""))
        assertNull(AetherNetTag.tryParse("INVALID"))
        assertNull(AetherNetTag.tryParse("IIIII-IIIII"))
        assertNull(AetherNetTag.tryParse("toolong-toolong"))
    }

    // ── isValid and toString ──────────────────────────────────────────────────

    @Test fun `isValid is true for a derived tag`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        assertTrue(tag.isValid)
    }

    @Test fun `isValid is false for empty-value AetherNetTag`() {
        val tag = AetherNetTag("")
        assertFalse(tag.isValid)
    }

    @Test fun `toString returns value`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        assertEquals(tag.value, tag.toString())
    }

    // ── fromPublicKey input validation ─────────────────────────────────────────

    @Test fun `fromPublicKey throws for key shorter than 32 bytes`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.fromPublicKey(ByteArray(16))
        }
    }

    @Test fun `fromPublicKey throws for key longer than 32 bytes`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.fromPublicKey(ByteArray(64))
        }
    }

    @Test fun `fromPublicKey throws for empty key`() {
        assertFailsWith<IllegalArgumentException> {
            AetherNetTag.fromPublicKey(ByteArray(0))
        }
    }

    // ── Sign-extension guard: high-byte keys must not corrupt bit packing ─────

    @Test fun `fromPublicKey handles keys with high-bit bytes without sign-extension`() {
        // All 0xFF bytes — every byte has the high bit set; without .and(0xFF) the
        // toLong() call would sign-extend and corrupt the 50-bit window.
        val key = ByteArray(32) { 0xFF.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        // Just verify it produces a well-formed tag — no exception, correct format
        assertEquals(11, tag.value.length)
        assertEquals('-', tag.value[5])
    }

    @Test fun `fromPublicKey key with 0x80 in first byte produces correct format`() {
        val key = ByteArray(32) { 0 }.also { it[0] = 0x80.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        assertEquals(11, tag.value.length)
        assertEquals('-', tag.value[5])
    }
}
