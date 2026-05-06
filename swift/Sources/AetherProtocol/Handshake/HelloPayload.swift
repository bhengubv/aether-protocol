// SPDX-License-Identifier: MIT

import Foundation

/// Wire payload carried inside a `PacketType.hello` or `PacketType.helloAck`
/// packet's `MeshPacket.payload`.
///
/// JSON shape (snake_case to match the rest of the Aether wire format and
/// the C# `HelloPayload` class):
///
///     {
///       "min_version": 1,
///       "max_version": 2,
///       "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
///       "implementation": "aether-swift/1.0.0"
///     }
///
/// Notes on security: this payload is NEITHER encrypted NOR authenticated by
/// design — the handshake runs before any Signal session exists. Peer
/// identity is verified later via Ed25519 packet signatures on the data
/// packets the peer subsequently sends. Treat the announced capabilities as
/// a hint, not as a security claim.
///
/// MUST stay byte-compatible with the C# `HelloPayload` (same field names,
/// same snake_case JSON keys).
public struct HelloPayload: Codable, Equatable, Sendable {
    /// Lowest protocol version the announcer can speak.
    public var minVersion: UInt8

    /// Highest protocol version the announcer can speak.
    public var maxVersion: UInt8

    /// Capability tags advertised by the announcer. Capability names are
    /// wire constants — case-sensitive, not human strings.
    public var capabilities: [String]

    /// Free-form implementation banner (e.g. `"aether-swift/1.0.0"`).
    /// Diagnostic only; not used for compatibility decisions.
    public var implementation: String

    public init(
        minVersion: UInt8 = 0,
        maxVersion: UInt8 = 0,
        capabilities: [String] = [],
        implementation: String = ""
    ) {
        self.minVersion = minVersion
        self.maxVersion = maxVersion
        self.capabilities = capabilities
        self.implementation = implementation
    }

    /// Explicit snake_case mapping. Matches the C# `JsonPropertyName`
    /// attributes exactly so cross-language interop holds.
    private enum CodingKeys: String, CodingKey {
        case minVersion = "min_version"
        case maxVersion = "max_version"
        case capabilities
        case implementation
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        // Tolerant of missing fields (defaults applied) and extra fields
        // (ignored), matching the C# JSON reader.
        let minRaw = try container.decodeIfPresent(Int.self, forKey: .minVersion) ?? 0
        let maxRaw = try container.decodeIfPresent(Int.self, forKey: .maxVersion) ?? 0
        guard (0 ... 255).contains(minRaw), (0 ... 255).contains(maxRaw) else {
            throw DecodingError.dataCorruptedError(
                forKey: .minVersion,
                in: container,
                debugDescription:
                    "HelloPayload version out of byte range: min=\(minRaw), max=\(maxRaw)"
            )
        }
        self.minVersion = UInt8(minRaw)
        self.maxVersion = UInt8(maxRaw)
        self.capabilities = try container.decodeIfPresent([String].self, forKey: .capabilities) ?? []
        self.implementation = try container.decodeIfPresent(String.self, forKey: .implementation) ?? ""
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(Int(minVersion), forKey: .minVersion)
        try container.encode(Int(maxVersion), forKey: .maxVersion)
        try container.encode(capabilities, forKey: .capabilities)
        try container.encode(implementation, forKey: .implementation)
    }

    /// Serialize to UTF-8 JSON bytes with snake_case keys, matching the
    /// C# `HelloPayloadJson.Options` (snake_case + ignore-null) byte-for-byte
    /// for the four-field shape we always emit.
    public func toJsonBytes() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(self)
    }

    /// Parse a UTF-8 JSON-encoded HelloPayload. Returns nil on malformed input,
    /// matching the C# behaviour of treating malformed Hello payloads as
    /// "ignore the packet" rather than throwing up the stack.
    public static func fromJsonBytes(_ data: Data) -> HelloPayload? {
        guard !data.isEmpty else { return nil }
        let decoder = JSONDecoder()
        return try? decoder.decode(HelloPayload.self, from: data)
    }
}
