// SPDX-License-Identifier: MIT

/// Fluent builder for ``AetherUri``. Use when programmatically constructing
/// an Aether URI from parts; for parsing an existing string, use ``AetherUri/parse(_:)``.
///
/// Example:
/// ```swift
/// let uri = try AetherUriBuilder()
///     .authority("KXJB7-MN2P4")
///     .path("content/sha256-abc123")
///     .query("codec", "opus")
///     .fragment("t=1m30s")
///     .build()
/// // -> aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s
/// ```
public final class AetherUriBuilder {

    // MARK: - State

    private var authorityValue: String?
    private var pathValue: String = ""
    private var queryValues: [String: String] = [:]
    private var queryOrder: [String] = []
    private var fragmentValue: String = ""

    // MARK: - Init

    public init() { }

    // MARK: - Fluent setters

    /// Sets the authority from an ``AetherNetTag``.
    @discardableResult
    public func authority(_ tag: AetherNetTag) throws -> AetherUriBuilder {
        guard tag.isValid else {
            throw AetherUri.AetherUriError.message("AetherTag is uninitialised.")
        }
        self.authorityValue = tag.value
        return self
    }

    /// Sets the authority from a raw string (AetherTag or 64-char hex UHID).
    @discardableResult
    public func authority(_ authority: String) throws -> AetherUriBuilder {
        if authority.isEmpty {
            throw AetherUri.AetherUriError.message("Authority is null or empty.")
        }
        // Validate by round-tripping through the parser.
        switch AetherUri.tryParse("aether://" + authority) {
        case .success(let u):
            self.authorityValue = u.authority
        case .failure(let err):
            throw err
        }
        return self
    }

    /// Sets the path component (without a leading slash).
    @discardableResult
    public func path(_ path: String) -> AetherUriBuilder {
        var trimmed = path
        while trimmed.hasPrefix("/") {
            trimmed.removeFirst()
        }
        self.pathValue = trimmed
        return self
    }

    /// Appends a single segment to the path.
    @discardableResult
    public func appendSegment(_ segment: String) -> AetherUriBuilder {
        if segment.isEmpty { return self }
        var trimmed = segment
        while trimmed.hasPrefix("/") {
            trimmed.removeFirst()
        }
        if self.pathValue.isEmpty {
            self.pathValue = trimmed
        } else {
            self.pathValue = self.pathValue + "/" + trimmed
        }
        return self
    }

    /// Adds or replaces a query parameter.
    @discardableResult
    public func query(_ key: String, _ value: String) throws -> AetherUriBuilder {
        if key.isEmpty {
            throw AetherUri.AetherUriError.message("Query key is null or empty.")
        }
        if self.queryValues[key] == nil {
            self.queryOrder.append(key)
        }
        self.queryValues[key] = value
        return self
    }

    /// Removes a query parameter by key.
    @discardableResult
    public func removeQuery(_ key: String) -> AetherUriBuilder {
        self.queryValues.removeValue(forKey: key)
        self.queryOrder.removeAll { $0 == key }
        return self
    }

    /// Sets the fragment (without leading `#`).
    @discardableResult
    public func fragment(_ fragment: String) -> AetherUriBuilder {
        var trimmed = fragment
        while trimmed.hasPrefix("#") {
            trimmed.removeFirst()
        }
        self.fragmentValue = trimmed
        return self
    }

    // MARK: - Build

    /// Builds the final ``AetherUri``. Throws if any component is invalid.
    public func build() throws -> AetherUri {
        guard let auth = authorityValue, !auth.isEmpty else {
            throw AetherUri.AetherUriError.message("Authority is required.")
        }
        // Round-trip through the parser to guarantee canonicalisation + validation.
        return try AetherUri.parse(render(authority: auth))
    }

    // MARK: - Debug rendering

    /// The URI string this builder currently represents (no validation).
    public var currentString: String {
        render(authority: authorityValue ?? "")
    }

    private func render(authority: String) -> String {
        if authority.isEmpty { return "" }
        var out = ""
        out.reserveCapacity(64)
        out.append("aether://")
        out.append(authority)
        if !pathValue.isEmpty {
            out.append("/")
            out.append(pathValue)
        }
        if !queryValues.isEmpty {
            out.append("?")
            var first = true
            for key in queryOrder {
                guard let value = queryValues[key] else { continue }
                if !first { out.append("&") }
                first = false
                out.append(key)
                if !value.isEmpty {
                    out.append("=")
                    out.append(value)
                }
            }
        }
        if !fragmentValue.isEmpty {
            out.append("#")
            out.append(fragmentValue)
        }
        return out
    }
}
