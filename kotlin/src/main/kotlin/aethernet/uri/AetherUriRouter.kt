// SPDX-License-Identifier: MIT

package aethernet.uri

import java.util.concurrent.ConcurrentHashMap

/**
 * Context delivered to a registered URI handler when its route matches. Carries
 * the original URI plus any captured route parameters.
 *
 * @property uri The dispatched URI.
 * @property handler The descriptor the URI matched.
 * @property routeParameters Captured route parameters from the matched template.
 */
data class DispatchContext(
    val uri: AetherUri,
    val handler: HandlerDescriptor,
    val routeParameters: Map<String, String>,
)

/**
 * Dispatches an incoming `aether://` URI to the registered handler for its route.
 * The router is per-app — each app constructs one with its own manifest.
 *
 * Handler callbacks are `suspend` functions; the router itself is `suspend`-aware
 * so callbacks can do real async work (mesh I/O, content fetch, etc.) without
 * blocking the dispatcher.
 *
 * ## Lifecycle
 *
 * 1. App startup: build a [HandlerManifest] describing every route the app accepts.
 * 2. App startup: register a callback per [HandlerDescriptor] via [registerHandler].
 * 3. At runtime: when a URI is received (incoming intent, deep link, in-mesh
 *    dispatch), call [dispatch] to invoke the right callback.
 */
interface IAetherUriRouter {
    /** The manifest the router resolves against. */
    val manifest: HandlerManifest

    /**
     * Register a callback for a handler descriptor. The descriptor must be one
     * present in [manifest]. Re-registering replaces the existing callback.
     */
    fun registerHandler(
        descriptor: HandlerDescriptor,
        callback: suspend (DispatchContext) -> Unit,
    )

    /**
     * Resolve and dispatch a URI. Returns `true` if a handler was matched AND
     * invoked; returns `false` if no handler matched or no callback was registered
     * for the matching descriptor. The handler's exceptions propagate to the caller.
     */
    suspend fun dispatch(uri: AetherUri): Boolean

    /**
     * Resolve and dispatch a URI given as a string. Throws [AetherUriException]
     * if the string fails to parse.
     */
    suspend fun dispatch(uri: String): Boolean
}

/**
 * Reference in-process implementation of [IAetherUriRouter]. Thread-safe — the
 * handler registry is backed by a [ConcurrentHashMap].
 */
class AetherUriRouter(override val manifest: HandlerManifest) : IAetherUriRouter {

    private val handlers =
        ConcurrentHashMap<HandlerDescriptor, suspend (DispatchContext) -> Unit>()

    override fun registerHandler(
        descriptor: HandlerDescriptor,
        callback: suspend (DispatchContext) -> Unit,
    ) {
        if (descriptor !in manifest.handlers) {
            throw AetherUriException(
                "Descriptor '${descriptor.name}' is not in the manifest."
            )
        }
        handlers[descriptor] = callback
    }

    override suspend fun dispatch(uri: AetherUri): Boolean {
        val (handler, captures) = manifest.resolve(uri) ?: return false
        val callback = handlers[handler] ?: return false
        val ctx = DispatchContext(uri, handler, captures)
        callback(ctx)
        return true
    }

    override suspend fun dispatch(uri: String): Boolean =
        dispatch(AetherUri.parse(uri))
}
