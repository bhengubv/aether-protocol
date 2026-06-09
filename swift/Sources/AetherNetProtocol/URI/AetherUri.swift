// SPDX-License-Identifier: MIT

/// Aether URI — the canonical addressing format for resources on the Aether mesh.
///
/// Grammar (ABNF, RFC 5234):
///
///     aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
///     authority    = aether-tag / uhid
///     aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
///     uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
///     path         = path-segment *( "/" path-segment )
///     path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
///     query        = query-param *( "&" query-param )
///     query-param  = key [ "=" value ]
///     key          = 1*( unreserved / pct-encoded )
///     value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
///     fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
///     crockford    = %x30-39 / %x41-48 / %x4A / %x4B / %x4D / %x4E / %x50-54 / %x56-5A
///                  ; 0–9 A-H J K M N P-T V-Z (no I L O U)
///     unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
///     pct-encoded  = "%" HEXDIG HEXDIG
///     sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
///
/// Components:
///   - **Scheme** is always `aether`. Case-insensitive on parse, lowercase on emit.
///   - **Authority** identifies the destination — an ``AetherNetTag`` (10 Crockford base-32
///     chars, dash optional) or a UHID (64 hex chars). Case-insensitive; canonicalised to
///     upper case.
///   - **Path** is opaque to the protocol — it names a handler within the destination
///     (e.g. `/content/<hash>`, `/profile`, `/inbox`). Case-sensitive.
///   - **Query** carries handler arguments. Keys preserve their parsed/inserted case; lookup
///     via ``queryValue(forKey:)`` is case-insensitive. Insertion order is preserved for
///     canonical encoding.
///   - **Fragment** is a client-side hint and is never transmitted over the wire
///     (e.g. `#t=1m30s` for a playback position).
///
/// Examples:
///
///     aether://KXJB7-MN2P4/profile
///     aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus
///     aether://KXJB7MN2P4/stream/live?bitrate=hd#t=1m30s
///     aether://a1b2c3d4e5f6...64hex/inbox
public struct AetherUri: Hashable, Equatable, CustomStringConvertible, Sendable {

    // MARK: - Errors

    public enum AetherUriError: Error, Equatable {
        case message(String)
    }

    // MARK: - Constants

    /// The fixed scheme name — `aether`.
    public static let scheme = "aether"

    private static let schemePrefix = "aether://"

    // MARK: - Storage

    /// The destination authority (AetherTag or UHID), canonicalised to upper case.
    public let authority: String

    /// The handler path, without the leading slash. Empty string means "root".
    public let path: String

    /// Decoded query parameters. Keys are case-insensitive on lookup via
    /// ``queryValue(forKey:)``; the underlying dictionary preserves the parsed case.
    public let query: [String: String]

    /// The fragment, with leading "#" stripped. Empty if none.
    public let fragment: String

    /// Insertion order of query keys — used to keep canonical encoding deterministic
    /// and byte-equal with the other language SDKs (C# / Rust / Go / TS / Python / …).
    private let queryOrder: [String]

    // MARK: - Init

    private init(
        authority: String,
        path: String,
        query: [String: String],
        queryOrder: [String],
        fragment: String
    ) {
        self.authority = authority
        self.path = path
        self.query = query
        self.queryOrder = queryOrder
        self.fragment = fragment
    }

    // MARK: - Parsing

    /// Parses an `aether://` URI. Throws ``AetherUriError/message(_:)`` on any
    /// syntactic violation. Use ``tryParse(_:)`` for non-throwing parses.
    public static func parse(_ s: String) throws -> AetherUri {
        switch tryParse(s) {
        case .success(let uri): return uri
        case .failure(let err): throw err
        }
    }

    /// Attempts to parse an `aether://` URI. Returns `.success` with the parsed URI
    /// or `.failure` with the error message.
    public static func tryParse(_ s: String) -> Result<AetherUri, AetherUriError> {
        if s.isEmpty {
            return .failure(.message("Input is null or empty."))
        }

        // Scheme is case-insensitive per RFC 3986.
        guard s.count >= schemePrefix.count else {
            return .failure(.message("Scheme must be '\(scheme)://'."))
        }
        let prefixEnd = s.index(s.startIndex, offsetBy: schemePrefix.count)
        let prefix = s[s.startIndex..<prefixEnd]
        guard prefix.lowercased() == schemePrefix else {
            return .failure(.message("Scheme must be '\(scheme)://'."))
        }

        var rest = String(s[prefixEnd...])

        // Split on fragment first (only one '#' is allowed).
        var fragment = ""
        if let hashIdx = rest.firstIndex(of: "#") {
            fragment = percentDecode(String(rest[rest.index(after: hashIdx)...]))
            rest = String(rest[rest.startIndex..<hashIdx])
        }

        // Then query.
        var query: [String: String] = [:]
        var queryOrder: [String] = []
        if let qIdx = rest.firstIndex(of: "?") {
            let queryRaw = String(rest[rest.index(after: qIdx)...])
            rest = String(rest[rest.startIndex..<qIdx])
            for pair in queryRaw.split(separator: "&", omittingEmptySubsequences: true) {
                let pairStr = String(pair)
                let key: String
                let value: String
                if let eqIdx = pairStr.firstIndex(of: "=") {
                    key = String(pairStr[pairStr.startIndex..<eqIdx])
                    value = String(pairStr[pairStr.index(after: eqIdx)...])
                } else {
                    key = pairStr
                    value = ""
                }
                let decodedKey = percentDecode(key)
                if decodedKey.isEmpty {
                    return .failure(.message("Empty query parameter key."))
                }
                let decodedValue = percentDecode(value)
                if query[decodedKey] == nil {
                    queryOrder.append(decodedKey)
                }
                query[decodedKey] = decodedValue
            }
        }

        // Then path.
        let authorityRaw: String
        let pathRaw: String
        if let slashIdx = rest.firstIndex(of: "/") {
            authorityRaw = String(rest[rest.startIndex..<slashIdx])
            pathRaw = String(rest[rest.index(after: slashIdx)...])
        } else {
            authorityRaw = rest
            pathRaw = ""
        }

        if authorityRaw.isEmpty {
            return .failure(.message("Authority is missing."))
        }

        // Authority validation: either an AetherNetTag or a 64-char hex UHID.
        let authorityResult = canonicaliseAuthority(authorityRaw)
        let authority: String
        switch authorityResult {
        case .success(let a): authority = a
        case .failure(let e): return .failure(e)
        }

        // Path validation: segments must contain only allowed characters.
        if let pathError = validatePath(pathRaw) {
            return .failure(.message(pathError))
        }

        let decodedPath = percentDecodePath(pathRaw)
        return .success(AetherUri(
            authority: authority,
            path: decodedPath,
            query: query,
            queryOrder: queryOrder,
            fragment: fragment
        ))
    }

    private static func canonicaliseAuthority(_ raw: String) -> Result<String, AetherUriError> {
        // Try UHID (64 hex chars).
        if raw.count == 64 && isHex(raw) {
            return .success(raw.uppercased())
        }

        // Try AetherTag (10 Crockford chars with optional dash).
        if let tag = AetherNetTag.tryParse(raw) {
            return .success(tag.value)
        }

        return .failure(.message(
            "Authority '\(raw)' is neither a valid AetherTag nor a 64-char hex UHID."
        ))
    }

    private static func isHex(_ s: String) -> Bool {
        for ch in s.utf8 {
            let c = Character(UnicodeScalar(ch))
            if !isHexChar(c) { return false }
        }
        return true
    }

    private static func validatePath(_ path: String) -> String? {
        if path.isEmpty { return nil }

        // Walk segments, allow unreserved + pct-encoded + sub-delims + ':' + '@'.
        for segmentSub in path.split(separator: "/", omittingEmptySubsequences: false) {
            let segment = String(segmentSub)
            if segment.isEmpty {
                return "Empty path segment (consecutive slashes)."
            }
            let chars = Array(segment)
            var i = 0
            while i < chars.count {
                let c = chars[i]
                if isUnreserved(c) || isSubDelim(c) || c == ":" || c == "@" {
                    i += 1
                    continue
                }
                if c == "%" {
                    if i + 2 >= chars.count || !isHexChar(chars[i + 1]) || !isHexChar(chars[i + 2]) {
                        return "Malformed percent-encoding at position \(i) of segment '\(segment)'."
                    }
                    i += 3
                    continue
                }
                return "Illegal character '\(c)' in path segment '\(segment)'."
            }
        }
        return nil
    }

    // MARK: - Character classes

    private static func isUnreserved(_ c: Character) -> Bool {
        guard let a = c.asciiValue else { return false }
        return (a >= 0x41 && a <= 0x5A) ||  // A–Z
               (a >= 0x61 && a <= 0x7A) ||  // a–z
               (a >= 0x30 && a <= 0x39) ||  // 0–9
               c == "-" || c == "." || c == "_" || c == "~"
    }

    private static func isSubDelim(_ c: Character) -> Bool {
        c == "!" || c == "$" || c == "&" || c == "'" || c == "(" || c == ")" ||
        c == "*" || c == "+" || c == "," || c == ";" || c == "="
    }

    private static func isHexChar(_ c: Character) -> Bool {
        guard let a = c.asciiValue else { return false }
        return (a >= 0x30 && a <= 0x39) ||  // 0–9
               (a >= 0x41 && a <= 0x46) ||  // A–F
               (a >= 0x61 && a <= 0x66)     // a–f
    }

    // MARK: - Path helpers

    /// Returns the path split into segments (already percent-decoded). Returns an empty
    /// array for the root path.
    public var pathSegments: [String] {
        if path.isEmpty { return [] }
        return path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
    }

    /// Returns the first path segment (the "handler name"), or empty string for root.
    public var handlerName: String {
        if path.isEmpty { return "" }
        if let slash = path.firstIndex(of: "/") {
            return String(path[path.startIndex..<slash])
        }
        return path
    }

    /// Case-insensitive query lookup.
    public func queryValue(forKey key: String) -> String? {
        if let v = query[key] { return v }
        let lower = key.lowercased()
        for (k, v) in query where k.lowercased() == lower {
            return v
        }
        return nil
    }

    // MARK: - Encoding

    /// Returns the canonical string form of this URI. Two URIs that compare equal
    /// produce the same canonical string.
    public var description: String {
        var out = ""
        out.reserveCapacity(64)
        out.append(Self.schemePrefix)
        out.append(authority)
        if !path.isEmpty {
            out.append("/")
            // Re-encode the path so the output is RFC-safe.
            Self.encodePath(into: &out, path: path)
        }
        if !query.isEmpty {
            out.append("?")
            var first = true
            for key in queryOrder {
                guard let value = query[key] else { continue }
                if !first { out.append("&") }
                first = false
                Self.encodeComponent(into: &out, value: key, kind: .queryKey)
                if !value.isEmpty {
                    out.append("=")
                    Self.encodeComponent(into: &out, value: value, kind: .queryValue)
                }
            }
        }
        if !fragment.isEmpty {
            out.append("#")
            Self.encodeComponent(into: &out, value: fragment, kind: .fragment)
        }
        return out
    }

    private enum EncodeKind {
        case pathSegment
        case queryKey
        case queryValue
        case fragment
    }

    private static func encodePath(into out: inout String, path: String) {
        var first = true
        for segmentSub in path.split(separator: "/", omittingEmptySubsequences: false) {
            if !first { out.append("/") }
            first = false
            encodeComponent(into: &out, value: String(segmentSub), kind: .pathSegment)
        }
    }

    private static func encodeComponent(into out: inout String, value: String, kind: EncodeKind) {
        for scalar in value.unicodeScalars {
            // Single-char scalars in the unreserved set we can handle by character.
            if let ascii = Character(scalar).asciiValue, scalar.value < 128 {
                let c = Character(scalar)
                if isAllowedUnencoded(c, kind: kind) {
                    out.append(c)
                    continue
                }
                // Percent-encode the single ASCII byte.
                out.append("%")
                out.append(hexUpper(ascii))
                continue
            }
            // Non-ASCII: percent-encode UTF-8 bytes.
            for byte in String(scalar).utf8 {
                out.append("%")
                out.append(hexUpper(byte))
            }
        }
    }

    private static func hexUpper(_ b: UInt8) -> String {
        let hi = Int((b >> 4) & 0x0F)
        let lo = Int(b & 0x0F)
        let alphabet: [Character] =
            ["0", "1", "2", "3", "4", "5", "6", "7",
             "8", "9", "A", "B", "C", "D", "E", "F"]
        return String([alphabet[hi], alphabet[lo]])
    }

    private static func isAllowedUnencoded(_ c: Character, kind: EncodeKind) -> Bool {
        if isUnreserved(c) { return true }
        switch kind {
        case .pathSegment:
            // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
            return isSubDelim(c) || c == ":" || c == "@"
        case .queryKey:
            // Always encode '&' and '=' in keys; allow ':' '@' and sub-delims that don't
            // collide with the query syntax.
            return c == ":" || c == "@" ||
                   c == "!" || c == "$" || c == "'" || c == "(" || c == ")" ||
                   c == "*" || c == "+" || c == "," || c == ";"
        case .queryValue:
            // Allow sub-delims except '&' (separator); '=' is fine inside a value.
            return c == ":" || c == "@" || c == "/" || c == "?" ||
                   c == "!" || c == "$" || c == "'" || c == "(" || c == ")" ||
                   c == "*" || c == "+" || c == "," || c == ";" || c == "="
        case .fragment:
            // fragment = *( pchar / "/" / "?" )  ; pchar incl. ':' '@' sub-delims
            return isSubDelim(c) || c == ":" || c == "@" || c == "/" || c == "?"
        }
    }

    // MARK: - Percent decode

    private static func percentDecode(_ input: String) -> String {
        if !input.contains("%") { return input }

        var bytes: [UInt8] = []
        bytes.reserveCapacity(input.utf8.count)
        let chars = Array(input)
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c == "%" && i + 2 < chars.count &&
               isHexChar(chars[i + 1]) && isHexChar(chars[i + 2]) {
                bytes.append(UInt8((hexValue(chars[i + 1]) << 4) | hexValue(chars[i + 2])))
                i += 3
            } else {
                // Non-encoded character — emit its UTF-8 bytes.
                for b in String(c).utf8 { bytes.append(b) }
                i += 1
            }
        }
        return String(decoding: bytes, as: UTF8.self)
    }

    private static func percentDecodePath(_ path: String) -> String {
        if path.isEmpty { return path }
        // Decode each segment independently so '/' isn't lost.
        let segs = path.split(separator: "/", omittingEmptySubsequences: false)
        return segs.map { percentDecode(String($0)) }.joined(separator: "/")
    }

    private static func hexValue(_ c: Character) -> Int {
        guard let a = c.asciiValue else { return 0 }
        if a <= 0x39 { return Int(a) - 0x30 }       // '0'–'9'
        if a <= 0x46 { return Int(a) - 0x41 + 10 }  // 'A'–'F'
        return Int(a) - 0x61 + 10                   // 'a'–'f'
    }

    // MARK: - Equatable & Hashable

    public static func == (lhs: AetherUri, rhs: AetherUri) -> Bool {
        lhs.authority == rhs.authority &&
        lhs.path == rhs.path &&
        lhs.fragment == rhs.fragment &&
        lhs.query == rhs.query
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(authority)
        hasher.combine(path)
        hasher.combine(fragment)
        hasher.combine(query)
    }
}
