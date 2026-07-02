// SPDX-License-Identifier: MIT

// Wire binding for PacketType.forgeAnnounce (41) — the transport half of the
// aether-forge package-cache extension. A node broadcasts this when it caches a
// new package artifact so mesh peers with the aethernet.forge/v1 capability learn
// where the artifact lives. Port of the C# reference
// (AetherNet.Forge.ForgeAnnounceService).
//
// Wire payload: JSON, snake_case keys, field order
//   package_id, content_hash, size_bytes, announced_at_ms
// size_bytes and announced_at_ms are bare integers.
// Byte-identity gate: fixtures/forge/vectors.json.

import Foundation

/// Event surfaced when a forge cache-entry announcement arrives from a peer.
/// Mirrors the C# `ForgeAnnouncePayload` that `AnnounceReceived` carries.
public struct ForgeAnnouncement: Sendable, Equatable {
    /// Package coordinate, e.g. `npm:react@18.2.0`.
    public let packageId: String
    /// Aether content hash of the cached artifact.
    public let contentHash: String
    /// Artifact size in bytes.
    public let sizeBytes: Int64
    /// Unix-ms timestamp the announcer cached the artifact.
    public let announcedAtMs: Int64

    public init(packageId: String, contentHash: String, sizeBytes: Int64, announcedAtMs: Int64) {
        self.packageId = packageId
        self.contentHash = contentHash
        self.sizeBytes = sizeBytes
        self.announcedAtMs = announcedAtMs
    }
}

/// Binds ``PacketType/forgeAnnounce`` (41) to the mesh: broadcast a freshly-cached
/// artifact announcement, and surface inbound announcements via ``onAnnounceReceived``
/// (the host records them in its ``ForgeServiceProtocol``). Transport for the
/// aether-forge package-cache extension. Mirrors C# `ForgeAnnounceService`.
public actor ForgeAnnounceService {
    private let sender: any MeshSender

    /// Raised when a forge announcement arrives from a peer.
    public var onAnnounceReceived: (@Sendable (ForgeAnnouncement) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnAnnounceReceived(_ callback: (@Sendable (ForgeAnnouncement) -> Void)?) {
        onAnnounceReceived = callback
    }

    /// Announce a cached artifact to mesh peers. Returns the number of peers reached.
    /// No-op (returns 0) for an empty package id.
    @discardableResult
    public func broadcast(
        packageId: String,
        contentHash: String,
        sizeBytes: Int64,
        announcedAtMs: Int64
    ) async -> Int {
        guard !packageId.isEmpty else { return 0 }
        let packet = MeshPacket(
            type: .forgeAnnounce,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: encodeForgeAnnounceWire(
                packageId: packageId,
                contentHash: contentHash,
                sizeBytes: sizeBytes,
                announcedAtMs: announcedAtMs
            )
        )
        return await sender.broadcast(packet)
    }

    /// Process an inbound ``PacketType/forgeAnnounce``. Fires ``onAnnounceReceived``.
    /// Returns false on the wrong packet type or a malformed / empty-package-id payload.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .forgeAnnounce else { return false }
        guard let ann = parseForgeAnnounceWire(packet.payload), !ann.packageId.isEmpty else {
            return false
        }
        onAnnounceReceived?(ann)
        return true
    }
}

// ─── ForgeAnnounce wire (PacketType 41) ───
//
// Hand-built in exact field order (package_id, content_hash, size_bytes, announced_at_ms)
// so cross-language byte-identity does not depend on JSONEncoder key ordering. Decode uses
// JSONDecoder (order-independent).

private struct ForgeAnnounceWire: Codable {
    let package_id: String
    let content_hash: String
    let size_bytes: Int64
    let announced_at_ms: Int64
    private enum CodingKeys: String, CodingKey {
        case package_id, content_hash, size_bytes, announced_at_ms
    }
}

private func jsonEscaped(_ s: String) -> String {
    var out = "\""
    for scalar in s.unicodeScalars {
        switch scalar {
        case "\"": out += "\\\""
        case "\\": out += "\\\\"
        case "\n": out += "\\n"
        case "\r": out += "\\r"
        case "\t": out += "\\t"
        default:
            if scalar.value < 0x20 { out += String(format: "\\u%04x", scalar.value) }
            else { out.unicodeScalars.append(scalar) }
        }
    }
    out += "\""
    return out
}

private func encodeForgeAnnounceWire(
    packageId: String,
    contentHash: String,
    sizeBytes: Int64,
    announcedAtMs: Int64
) -> Data {
    let json = "{\"package_id\":\(jsonEscaped(packageId)),"
        + "\"content_hash\":\(jsonEscaped(contentHash)),"
        + "\"size_bytes\":\(sizeBytes),"
        + "\"announced_at_ms\":\(announcedAtMs)}"
    return Data(json.utf8)
}

private func parseForgeAnnounceWire(_ data: Data) -> ForgeAnnouncement? {
    guard let w = try? JSONDecoder().decode(ForgeAnnounceWire.self, from: data) else { return nil }
    return ForgeAnnouncement(
        packageId: w.package_id,
        contentHash: w.content_hash,
        sizeBytes: w.size_bytes,
        announcedAtMs: w.announced_at_ms
    )
}

/// Test-only shim exposing the real ``ForgeAnnounce`` wire encoder (the encoder stays
/// `private`) so byte-identity vectors in `fixtures/forge/vectors.json` can be verified.
internal func _forgeAnnounceWireBytesForTests(
    packageId: String,
    contentHash: String,
    sizeBytes: Int64,
    announcedAtMs: Int64
) -> Data {
    encodeForgeAnnounceWire(
        packageId: packageId,
        contentHash: contentHash,
        sizeBytes: sizeBytes,
        announcedAtMs: announcedAtMs
    )
}
