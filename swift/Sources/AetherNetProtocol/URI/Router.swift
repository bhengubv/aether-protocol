// SPDX-License-Identifier: MIT

/// Context delivered to a registered URI handler when its route matches.
/// Carries the original URI plus any captured route parameters.
public struct DispatchContext: Sendable {

    public let uri: AetherUri
    public let handler: HandlerDescriptor
    public let routeParameters: [String: String]

    public init(
        uri: AetherUri,
        handler: HandlerDescriptor,
        routeParameters: [String: String]
    ) {
        self.uri = uri
        self.handler = handler
        self.routeParameters = routeParameters
    }
}

/// Async callback fired when a URI matches a registered handler.
public typealias AetherUriHandler = @Sendable (DispatchContext) async throws -> Void

/// Dispatches an incoming `aether://` URI to the registered handler for its
/// route. The router is per-app — each app constructs one with its own manifest.
///
/// Lifecycle:
///   1. App startup: build a ``HandlerManifest`` describing every route the app accepts.
///   2. App startup: register a callback per ``HandlerDescriptor`` via ``registerHandler(_:handler:)``.
///   3. At runtime: when a URI is received (incoming intent, deep link, or in-mesh dispatch),
///      call ``dispatch(_:)`` or ``dispatch(string:)`` to invoke the right callback.
public protocol AetherUriRouting: Sendable {

    /// The manifest the router resolves against. Nonisolated; safe to read
    /// from any context — actor adopters expose it as `nonisolated let`.
    nonisolated var manifest: HandlerManifest { get }

    /// Registers a callback for a handler descriptor. The descriptor must be one
    /// present in ``manifest``. Re-registering replaces the existing callback.
    func registerHandler(_ descriptor: HandlerDescriptor, handler: @escaping AetherUriHandler) async throws

    /// Resolves and dispatches a URI. Returns `true` if a handler was matched AND
    /// invoked; returns `false` if no handler matched. The handler's errors propagate.
    @discardableResult
    func dispatch(_ uri: AetherUri) async throws -> Bool

    /// Resolves and dispatches a URI given as a string. Throws ``AetherUri/AetherUriError``
    /// if the string fails to parse.
    @discardableResult
    func dispatch(string uri: String) async throws -> Bool
}

/// Reference in-process implementation of ``AetherUriRouting``. Thread-safe via Swift actor.
public final actor AetherUriRouter: AetherUriRouting {

    // MARK: - State

    public nonisolated let manifest: HandlerManifest

    private var handlers: [HandlerDescriptor: AetherUriHandler] = [:]

    // MARK: - Init

    public init(manifest: HandlerManifest) {
        self.manifest = manifest
    }

    // MARK: - Registration

    public func registerHandler(
        _ descriptor: HandlerDescriptor,
        handler: @escaping AetherUriHandler
    ) throws {
        if !manifest.handlers.contains(descriptor) {
            throw AetherUri.AetherUriError.message(
                "Descriptor '\(descriptor.name)' is not in the manifest."
            )
        }
        handlers[descriptor] = handler
    }

    // MARK: - Dispatch

    @discardableResult
    public func dispatch(_ uri: AetherUri) async throws -> Bool {
        guard let resolved = manifest.resolve(uri) else { return false }
        guard let callback = handlers[resolved.0] else { return false }
        let ctx = DispatchContext(
            uri: uri,
            handler: resolved.0,
            routeParameters: resolved.1
        )
        try await callback(ctx)
        return true
    }

    @discardableResult
    public func dispatch(string uri: String) async throws -> Bool {
        let parsed = try AetherUri.parse(uri)
        return try await dispatch(parsed)
    }
}
