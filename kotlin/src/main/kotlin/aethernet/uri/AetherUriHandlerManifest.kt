// SPDX-License-Identifier: MIT

package aethernet.uri

/**
 * Describes a single handler an app exposes on its `aether://` URI surface.
 *
 * A handler is identified by its first path segment ([name]) plus an optional
 * [pathTemplate] that captures route parameters. The router matches an incoming
 * URI's [AetherUri.handlerName] + path against this manifest and dispatches
 * accordingly.
 *
 * ## Path template syntax
 * ```
 *   "content/{hash}"             // matches /content/abc
 *   "watch/{sessionId}/join"     // matches /watch/123/join
 *   "profile"                    // matches /profile exactly
 *   "profile/avatar"             // matches /profile/avatar exactly
 * ```
 *
 * @property name Handler name — the first path segment (e.g. "content", "stream").
 * @property pathTemplate Path template (e.g. "content/{hash}") — empty for a root handler.
 * @property expectedQueryKeys Optional list of expected query keys (informational).
 * @property description Human-readable description for diagnostics + docs.
 */
data class HandlerDescriptor(
    val name: String,
    val pathTemplate: String = "",
    val expectedQueryKeys: List<String> = emptyList(),
    val description: String = "",
) {
    init {
        if (name.isBlank()) throw AetherUriException("HandlerName is required.")
    }

    /**
     * Matches an incoming URI's path against this descriptor's template. Returns
     * the captured route parameters on success, or `null` on no match.
     */
    fun match(path: String): Map<String, String>? {
        val templateSegs: List<String> = if (pathTemplate.isEmpty()) {
            listOf(name)
        } else {
            (name + "/" + pathTemplate.trimStart('/')).split('/')
        }
        val pathSegs = path.split('/')
        if (templateSegs.size != pathSegs.size) return null

        // Capture keys preserve the template's original casing (the C# reference
        // stores them in an OrdinalIgnoreCase dictionary; callers typically look
        // up by the same spelling they wrote in the template, so preserving case
        // matches that ergonomic).
        val captures = LinkedHashMap<String, String>()
        for (i in templateSegs.indices) {
            val t = templateSegs[i]
            val p = pathSegs[i]
            if (t.length >= 2 && t[0] == '{' && t[t.length - 1] == '}') {
                captures[t.substring(1, t.length - 1)] = p
            } else if (t != p) {
                return null
            }
        }
        return captures
    }
}

/**
 * An app's complete `aether://` handler manifest — the set of paths it accepts.
 * Each app registers exactly one manifest at startup; the router dispatches
 * against it.
 *
 * @property appId The owning app's identifier (e.g. "aether.media", "aether.txtme").
 * @property handlers All registered handler descriptors.
 */
data class HandlerManifest(
    val appId: String,
    val handlers: List<HandlerDescriptor>,
) {
    init {
        if (appId.isBlank()) throw AetherUriException("AppId is required.")
    }

    /**
     * Resolves an incoming URI against this manifest. Returns the matched
     * descriptor and its captured route parameters, or `null` if no handler
     * matched.
     */
    fun resolve(uri: AetherUri): Pair<HandlerDescriptor, Map<String, String>>? {
        for (h in handlers) {
            if (h.name != uri.handlerName) continue
            val captures = h.match(uri.path) ?: continue
            return h to captures
        }
        return null
    }
}
