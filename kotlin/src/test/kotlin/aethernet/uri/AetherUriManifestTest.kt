// SPDX-License-Identifier: MIT

package aethernet.uri

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * Unit tests for [HandlerDescriptor], [HandlerManifest], and [AetherUriRouter].
 * Mirrors `AetherUriHandlerManifestTests.cs` plus extras covering Kotlin's
 * coroutine-based dispatch surface and the thread-safe handler registry.
 */
class AetherUriManifestTest {

    private fun sampleManifest(): HandlerManifest = HandlerManifest(
        appId = "aether.media",
        handlers = listOf(
            HandlerDescriptor("profile", description = "Get the profile."),
            HandlerDescriptor("profile", "avatar", description = "Get the avatar."),
            HandlerDescriptor("content", "{hash}", description = "Fetch content."),
            HandlerDescriptor("watch", "{sessionId}/join", description = "Join watch party."),
        ),
    )

    // ── HandlerDescriptor.match ─────────────────────────────────────────────

    @Test fun `descriptor empty template matches exact handler name`() {
        val d = HandlerDescriptor("profile")
        val captures = d.match("profile")
        assertNotNull(captures)
        assertTrue(captures.isEmpty())
    }

    @Test fun `descriptor empty template does not match longer path`() {
        val d = HandlerDescriptor("profile")
        assertNull(d.match("profile/avatar"))
    }

    @Test fun `descriptor literal template matches`() {
        val d = HandlerDescriptor("profile", "avatar")
        val captures = d.match("profile/avatar")
        assertNotNull(captures)
        assertTrue(captures.isEmpty())
    }

    @Test fun `descriptor capture template populates parameter`() {
        val d = HandlerDescriptor("content", "{hash}")
        val captures = d.match("content/sha256-abc")
        assertNotNull(captures)
        assertEquals("sha256-abc", captures["hash"])
    }

    @Test fun `descriptor multi-segment capture populates parameter`() {
        val d = HandlerDescriptor("watch", "{sessionId}/join")
        val captures = d.match("watch/sess-99/join")
        assertNotNull(captures)
        assertEquals("sess-99", captures["sessionId"])
    }

    @Test fun `descriptor wrong path-length returns null`() {
        val d = HandlerDescriptor("watch", "{sessionId}/join")
        assertNull(d.match("watch/sess-99"))
    }

    @Test fun `descriptor blank name throws`() {
        assertFailsWith<AetherUriException> { HandlerDescriptor("") }
        assertFailsWith<AetherUriException> { HandlerDescriptor("   ") }
    }

    @Test fun `descriptor preserves description and expected query keys`() {
        val d = HandlerDescriptor(
            "content",
            "{hash}",
            expectedQueryKeys = listOf("codec", "bitrate"),
            description = "Fetch content."
        )
        assertEquals(listOf("codec", "bitrate"), d.expectedQueryKeys)
        assertEquals("Fetch content.", d.description)
    }

    // ── HandlerManifest.resolve ─────────────────────────────────────────────

    @Test fun `manifest exact match resolves`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/profile")
        val r = m.resolve(u)
        assertNotNull(r)
        assertEquals("profile", r.first.name)
        assertEquals("", r.first.pathTemplate)
        assertTrue(r.second.isEmpty())
    }

    @Test fun `manifest nested exact match resolves`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/profile/avatar")
        val r = m.resolve(u)
        assertNotNull(r)
        assertEquals("avatar", r.first.pathTemplate)
    }

    @Test fun `manifest route capture populates parameter`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/content/sha256-abc")
        val r = m.resolve(u)
        assertNotNull(r)
        assertEquals("sha256-abc", r.second["hash"])
    }

    @Test fun `manifest multi-segment capture populates parameter`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/watch/sess-99/join")
        val r = m.resolve(u)
        assertNotNull(r)
        assertEquals("sess-99", r.second["sessionId"])
    }

    @Test fun `manifest unknown handler returns null`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/unknown")
        assertNull(m.resolve(u))
    }

    @Test fun `manifest wrong path length returns null`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4/watch/sess-99")
        assertNull(m.resolve(u))
    }

    @Test fun `manifest root path returns null`() {
        val m = sampleManifest()
        val u = AetherUri.parse("aether://KXJB7-MN2P4")
        // Root authority — no handler name.
        assertNull(m.resolve(u))
    }

    @Test fun `manifest appId required on construction`() {
        assertFailsWith<AetherUriException> { HandlerManifest("", emptyList()) }
        assertFailsWith<AetherUriException> { HandlerManifest("   ", emptyList()) }
    }

    @Test fun `manifest picks first matching descriptor`() {
        // Two descriptors with the same name+template — first wins.
        val m = HandlerManifest(
            "test.app",
            listOf(
                HandlerDescriptor("x", description = "first"),
                HandlerDescriptor("x", description = "second"),
            ),
        )
        val u = AetherUri.parse("aether://KXJB7-MN2P4/x")
        assertEquals("first", m.resolve(u)!!.first.description)
    }

    // ── Router ──────────────────────────────────────────────────────────────

    @Test fun `router dispatch invokes registered callback`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val profileHandler = m.handlers[0]
        var invoked = false
        router.registerHandler(profileHandler) { invoked = true }

        val ok = router.dispatch("aether://KXJB7-MN2P4/profile")
        assertTrue(ok)
        assertTrue(invoked)
    }

    @Test fun `router dispatch no match returns false`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val ok = router.dispatch("aether://KXJB7-MN2P4/nope")
        assertFalse(ok)
    }

    @Test fun `router dispatch context carries route parameters`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val contentHandler = m.handlers[2] // /content/{hash}
        var seen: DispatchContext? = null
        router.registerHandler(contentHandler) { ctx -> seen = ctx }

        router.dispatch("aether://KXJB7-MN2P4/content/sha256-xyz")
        assertNotNull(seen)
        assertEquals("sha256-xyz", seen!!.routeParameters["hash"])
        assertSame(contentHandler, seen!!.handler)
    }

    @Test fun `router register descriptor not in manifest throws`() {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val alien = HandlerDescriptor("stranger")
        assertFailsWith<AetherUriException> {
            router.registerHandler(alien) { }
        }
    }

    @Test fun `router dispatch no callback returns false`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        // /profile is in the manifest but no callback registered.
        val ok = router.dispatch("aether://KXJB7-MN2P4/profile")
        assertFalse(ok)
    }

    @Test fun `router dispatch propagates handler exception`() {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val h = m.handlers[0]
        router.registerHandler(h) { throw IllegalStateException("boom") }
        val ex = assertFailsWith<IllegalStateException> {
            runBlocking { router.dispatch("aether://KXJB7-MN2P4/profile") }
        }
        assertEquals("boom", ex.message)
    }

    @Test fun `router re-register replaces previous callback`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val h = m.handlers[0]
        var firstInvoked = false
        var secondInvoked = false
        router.registerHandler(h) { firstInvoked = true }
        router.registerHandler(h) { secondInvoked = true }

        router.dispatch("aether://KXJB7-MN2P4/profile")
        assertFalse(firstInvoked)
        assertTrue(secondInvoked)
    }

    @Test fun `router dispatch by AetherUri delegates to resolve`() = runBlocking {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        val h = m.handlers[0]
        var invoked = false
        router.registerHandler(h) { invoked = true }
        val u = AetherUri.parse("aether://KXJB7-MN2P4/profile")
        assertTrue(router.dispatch(u))
        assertTrue(invoked)
    }

    @Test fun `router dispatch bad uri string throws AetherUriException`() {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        assertFailsWith<AetherUriException> {
            runBlocking { router.dispatch("not-a-uri") }
        }
    }

    @Test fun `router manifest exposed`() {
        val m = sampleManifest()
        val router = AetherUriRouter(m)
        assertSame(m, router.manifest)
    }
}
