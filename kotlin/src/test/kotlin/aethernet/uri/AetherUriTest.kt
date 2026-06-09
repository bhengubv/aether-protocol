// SPDX-License-Identifier: MIT

package aethernet.uri

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * Unit tests for [AetherUri] — parsing, canonical encoding, accessors, equality.
 *
 * Mirrors the C# `AetherUriParseTests.cs` test surface plus a few Kotlin-specific
 * round-trip cases.
 */
class AetherUriTest {

    // ── Happy-path parsing ──────────────────────────────────────────────────

    @Test fun `parse authority only succeeds`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4")
        assertEquals("KXJB7-MN2P4", u.authority)
        assertEquals("", u.path)
        assertTrue(u.query.isEmpty())
        assertEquals("", u.fragment)
    }

    @Test fun `parse authority without dash canonicalises to with dash`() {
        val u = AetherUri.parse("aether://KXJB7MN2P4")
        assertEquals("KXJB7-MN2P4", u.authority)
    }

    @Test fun `parse authority lowercase canonicalises to upper`() {
        val u = AetherUri.parse("aether://kxjb7-mn2p4")
        assertEquals("KXJB7-MN2P4", u.authority)
    }

    @Test fun `parse authority with path succeeds`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/profile")
        assertEquals("profile", u.path)
        assertEquals("profile", u.handlerName)
    }

    @Test fun `parse authority with multi-segment path succeeds`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/content/sha256-abc123")
        assertEquals("content/sha256-abc123", u.path)
        assertEquals("content", u.handlerName)
        assertEquals(listOf("content", "sha256-abc123"), u.pathSegments)
    }

    @Test fun `parse with query succeeds`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128")
        assertEquals("opus", u.query["codec"])
        assertEquals("128", u.query["bitrate"])
    }

    @Test fun `parse query key is case-insensitive`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/x?Codec=opus")
        assertEquals("opus", u.query["codec"])
        // Stored lower-case for canonical equality. Caller can write Codec / codec
        // either way and the parser folds to lower-case lookup.
        assertEquals("opus", u.query["codec"])
    }

    @Test fun `parse with fragment succeeds`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/stream/live#t=1m30s")
        assertEquals("t=1m30s", u.fragment)
    }

    @Test fun `parse with empty value query param treats as empty`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/x?flag")
        assertTrue(u.query.containsKey("flag"))
        assertEquals("", u.query["flag"])
    }

    @Test fun `parse uhid 64 hex succeeds`() {
        val hex = "a".repeat(64)
        val u = AetherUri.parse("aether://$hex/inbox")
        assertEquals(hex.uppercase(), u.authority)
        assertEquals("inbox", u.handlerName)
    }

    @Test fun `parse percent-encoded query decodes`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/inbox?title=hello%20world")
        assertEquals("hello world", u.query["title"])
    }

    @Test fun `parse percent-encoded path segment decodes`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/inbox/Hello%20World")
        assertEquals(listOf("inbox", "Hello World"), u.pathSegments)
    }

    @Test fun `parse percent-encoded utf8 decodes`() {
        // "café" → c, a, f, é (UTF-8 C3 A9)
        val u = AetherUri.parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9")
        assertEquals("café", u.query["title"])
    }

    @Test fun `parse fragment not in query`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4/x?a=b#frag")
        assertEquals("b", u.query["a"])
        assertEquals("frag", u.fragment)
    }

    @Test fun `parse scheme is case-insensitive`() {
        val u = AetherUri.parse("AETHER://KXJB7-MN2P4/profile")
        assertEquals("KXJB7-MN2P4", u.authority)
        assertEquals("profile", u.path)
    }

    @Test fun `pathSegments empty for root path`() {
        val u = AetherUri.parse("aether://KXJB7-MN2P4")
        assertEquals(emptyList(), u.pathSegments)
        assertEquals("", u.handlerName)
    }

    // ── Failure paths ────────────────────────────────────────────────────────

    @Test fun `tryParse empty string fails`() {
        assertTrue(AetherUri.tryParse("") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse wrong scheme fails`() {
        assertTrue(AetherUri.tryParse("http://KXJB7-MN2P4/") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse missing slashslash fails`() {
        assertTrue(AetherUri.tryParse("aether:KXJB7-MN2P4") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse single slash fails`() {
        assertTrue(AetherUri.tryParse("aether:/KXJB7-MN2P4") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse empty authority fails`() {
        assertTrue(AetherUri.tryParse("aether:///profile") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse bad authority fails`() {
        // 'I' is not a Crockford char.
        assertTrue(AetherUri.tryParse("aether://INVALID-AUTH1/x") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse too short authority fails`() {
        assertTrue(AetherUri.tryParse("aether://ABC") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse consecutive slashes in path fails`() {
        assertTrue(AetherUri.tryParse("aether://KXJB7-MN2P4/a//b") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse illegal path char fails`() {
        assertTrue(AetherUri.tryParse("aether://KXJB7-MN2P4/has space") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse malformed percent-encoding fails`() {
        assertTrue(AetherUri.tryParse("aether://KXJB7-MN2P4/inbox/%2") is AetherUri.ParseResult.Err)
    }

    @Test fun `tryParse empty query key fails`() {
        assertTrue(AetherUri.tryParse("aether://KXJB7-MN2P4/x?=value") is AetherUri.ParseResult.Err)
    }

    @Test fun `parse throws AetherUriException on bad input`() {
        assertFailsWith<AetherUriException> { AetherUri.parse("not-a-uri") }
    }

    @Test fun `tryParse Err carries message`() {
        val r = AetherUri.tryParse("http://X")
        assertTrue(r is AetherUri.ParseResult.Err)
        assertTrue(r.message.contains("Scheme"))
    }

    @Test fun `tryParse Ok carries uri`() {
        val r = AetherUri.tryParse("aether://KXJB7-MN2P4/profile")
        assertTrue(r is AetherUri.ParseResult.Ok)
        assertEquals("profile", r.uri.path)
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    @Test fun `roundtrip authority only is stable`() = roundTrip("aether://KXJB7-MN2P4")

    @Test fun `roundtrip authority with profile is stable`() =
        roundTrip("aether://KXJB7-MN2P4/profile")

    @Test fun `roundtrip with hash content is stable`() =
        roundTrip("aether://KXJB7-MN2P4/content/sha256-abc")

    @Test fun `roundtrip with fragment is stable`() =
        roundTrip("aether://KXJB7-MN2P4/stream/live#t=1m30s")

    @Test fun `toString encodes spaces`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("inbox")
            .query("title", "hello world")
            .build()
        assertTrue(u.toString().contains("hello%20world"))
    }

    @Test fun `toString encodes utf8 fragment`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .fragment("café")
            .build()
        assertTrue(u.toString().contains("%C3%A9"))
    }

    private fun roundTrip(input: String) {
        val parsed = AetherUri.parse(input)
        val rendered = parsed.toString()
        val reparsed = AetherUri.parse(rendered)
        assertEquals(parsed, reparsed)
        assertEquals(rendered, reparsed.toString())
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    @Test fun `equality same content equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x?k=v")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/x?k=v")
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
    }

    @Test fun `equality different authority not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x")
        val b = AetherUri.parse("aether://KXJB7-MN2P5/x")
        assertNotEquals(a, b)
    }

    @Test fun `equality different path not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/y")
        assertNotEquals(a, b)
    }

    @Test fun `equality different fragment not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x#a")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/x#b")
        assertNotEquals(a, b)
    }

    @Test fun `equality query order irrelevant`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x?a=1&b=2")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/x?b=2&a=1")
        assertEquals(a, b)
        // hashCode is order-insensitive too — order-equal hashes are a hard
        // contract.
        assertEquals(a.hashCode(), b.hashCode())
    }

    @Test fun `equality different query values not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x?a=1")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/x?a=2")
        assertNotEquals(a, b)
    }

    @Test fun `equality different query sizes not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x?a=1")
        val b = AetherUri.parse("aether://KXJB7-MN2P4/x?a=1&b=2")
        assertNotEquals(a, b)
    }

    @Test fun `equality reflexive`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x")
        assertSame(a, a)
        assertEquals(a, a)
    }

    @Test fun `equality null is not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x")
        assertFalse(a.equals(null))
    }

    @Test fun `equality other-type is not equal`() {
        val a = AetherUri.parse("aether://KXJB7-MN2P4/x")
        assertFalse(a.equals("aether://KXJB7-MN2P4/x"))
    }

    // ── SCHEME constant ─────────────────────────────────────────────────────

    @Test fun `scheme constant is aether`() {
        assertEquals("aether", AetherUri.SCHEME)
    }
}
