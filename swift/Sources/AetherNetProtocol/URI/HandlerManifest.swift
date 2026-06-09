// SPDX-License-Identifier: MIT

/// Describes a single handler an app exposes on its `aether://` URI surface.
///
/// A handler is identified by its first path segment (the `name`) plus
/// an optional path template that captures route parameters. The router matches an
/// incoming URI's ``AetherUri/handlerName`` + path against this manifest
/// and dispatches accordingly.
///
/// Path template syntax:
/// ```
///   "content/{hash}"             // matches /content/abc
///   "watch/{sessionId}/join"     // matches /watch/123/join
///   "profile"                    // matches /profile exactly
///   "profile/avatar"             // matches /profile/avatar exactly
/// ```
public struct HandlerDescriptor: Hashable, Sendable {

    /// Handler name — the first path segment (e.g. "content", "stream").
    public let name: String

    /// Path template (e.g. "{hash}") — empty for a root handler.
    public let pathTemplate: String

    /// Human-readable description for diagnostics + docs.
    public let description: String

    /// Optional list of expected query keys (informational).
    public let expectedQueryKeys: [String]

    public init(
        name: String,
        pathTemplate: String = "",
        description: String = "",
        expectedQueryKeys: [String] = []
    ) {
        self.name = name
        self.pathTemplate = pathTemplate
        self.description = description
        self.expectedQueryKeys = expectedQueryKeys
    }

    // MARK: - Matching

    /// Matches an incoming URI's path against this descriptor's template.
    /// Returns the captured route parameters on success, or `nil` on no match.
    public func match(_ path: String) -> [String: String]? {
        let templateSegs: [String]
        if pathTemplate.isEmpty {
            templateSegs = [name]
        } else {
            var trimmed = pathTemplate
            while trimmed.hasPrefix("/") {
                trimmed.removeFirst()
            }
            templateSegs = (name + "/" + trimmed)
                .split(separator: "/", omittingEmptySubsequences: false)
                .map(String.init)
        }
        let pathSegs = path
            .split(separator: "/", omittingEmptySubsequences: false)
            .map(String.init)
        guard templateSegs.count == pathSegs.count else { return nil }

        var captures: [String: String] = [:]
        for i in 0..<templateSegs.count {
            let t = templateSegs[i]
            let p = pathSegs[i]
            if t.count >= 2 && t.first == "{" && t.last == "}" {
                let start = t.index(after: t.startIndex)
                let end = t.index(before: t.endIndex)
                captures[String(t[start..<end])] = p
            } else if t != p {
                return nil
            }
        }
        return captures
    }
}

/// An app's complete `aether://` handler manifest — the set of paths it accepts.
/// Each app registers exactly one manifest at startup; the router dispatches against it.
public struct HandlerManifest: Sendable {

    /// The owning app's identifier (e.g. `"aether.media"`, `"aether.txtme"`).
    public let appId: String

    /// All registered handler descriptors.
    public let handlers: [HandlerDescriptor]

    public init(appId: String, handlers: [HandlerDescriptor]) {
        self.appId = appId
        self.handlers = handlers
    }

    /// Resolves an incoming URI against this manifest. Returns the matched descriptor
    /// and its captured route parameters, or `nil` if no handler matched.
    public func resolve(_ uri: AetherUri) -> (HandlerDescriptor, [String: String])? {
        for h in handlers where h.name == uri.handlerName {
            if let captures = h.match(uri.path) {
                return (h, captures)
            }
        }
        return nil
    }
}
