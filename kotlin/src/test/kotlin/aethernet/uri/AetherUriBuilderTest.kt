// SPDX-License-Identifier: MIT

package aethernet.uri

import aethernet.identity.AetherNetTag
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse

/**
 * Unit tests for [AetherUriBuilder] — the fluent surface for constructing
 * AetherUris from parts. Mirrors `AetherUriBuilderTests.cs`.
 */
class AetherUriBuilderTest {

    @Test fun `authority from AetherNetTag succeeds`() {
        val key = ByteArray(32) { it.toByte() }
        val tag = AetherNetTag.fromPublicKey(key)
        val u = AetherUriBuilder()
            .authority(tag)
            .path("profile")
            .build()
        assertEquals(tag.value, u.authority)
        assertEquals("profile", u.path)
    }

    @Test fun `fluent chain renders correctly`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("content/sha256-abc")
            .query("codec", "opus")
            .fragment("t=1m30s")
            .build()
        assertEquals(
            "aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s",
            u.toString(),
        )
    }

    @Test fun `appendSegment builds path`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .appendSegment("watch")
            .appendSegment("sess-99")
            .appendSegment("join")
            .build()
        assertEquals("watch/sess-99/join", u.path)
    }

    @Test fun `appendSegment ignores empty input`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .appendSegment("a")
            .appendSegment("")
            .appendSegment("b")
            .build()
        assertEquals("a/b", u.path)
    }

    @Test fun `removeQuery drops key`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("a", "1")
            .query("b", "2")
            .removeQuery("a")
            .build()
        assertFalse(u.query.containsKey("a"))
        assertEquals("2", u.query["b"])
    }

    @Test fun `removeQuery case-insensitive`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("Codec", "opus")
            .removeQuery("CODEC")
            .build()
        assertFalse(u.query.containsKey("codec"))
    }

    @Test fun `path strip leading slash`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("/profile")
            .build()
        assertEquals("profile", u.path)
    }

    @Test fun `fragment strip leading hash`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .fragment("#anchor")
            .build()
        assertEquals("anchor", u.fragment)
    }

    @Test fun `missing authority throws on build`() {
        assertFailsWith<AetherUriException> {
            AetherUriBuilder().path("x").build()
        }
    }

    @Test fun `bad authority string throws`() {
        assertFailsWith<AetherUriException> {
            AetherUriBuilder().authority("not-an-id")
        }
    }

    @Test fun `empty authority string throws`() {
        assertFailsWith<AetherUriException> {
            AetherUriBuilder().authority("")
        }
    }

    @Test fun `empty query key throws`() {
        assertFailsWith<AetherUriException> {
            AetherUriBuilder()
                .authority("KXJB7-MN2P4")
                .query("", "v")
        }
    }

    @Test fun `uninitialised tag throws`() {
        val empty = AetherNetTag("")
        assertFailsWith<AetherUriException> {
            AetherUriBuilder().authority(empty)
        }
    }

    @Test fun `builder toString without authority returns empty`() {
        assertEquals("", AetherUriBuilder().toString())
    }

    @Test fun `builder rebuilds query in insertion order`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("first", "1")
            .query("second", "2")
            .build()
        // LinkedHashMap preserves insertion order — toString must reflect that.
        assertEquals("aether://KXJB7-MN2P4/x?first=1&second=2", u.toString())
    }

    @Test fun `build round-trips through parser`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("inbox")
            .query("title", "hello world")
            .build()
        val reparsed = AetherUri.parse(u.toString())
        assertEquals(u, reparsed)
    }

    @Test fun `build encodes via parser even when input is unencoded`() {
        val u = AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("inbox")
            .query("title", "hello world")
            .build()
        // Builder calls Parse; the parser stores the decoded value, and toString
        // re-encodes — so the final string has %20, not a literal space.
        assertEquals("hello world", u.query["title"])
        assertEquals("aether://KXJB7-MN2P4/inbox?title=hello%20world", u.toString())
    }
}
