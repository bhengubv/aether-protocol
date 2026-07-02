// SPDX-License-Identifier: MIT

// Wire binding for PacketType.spaceBreadcrumb (40) — the transport half of the
// aether-space geo-pinned-noticeboard extension. A thin actor that BROADCASTS a
// locally-dropped breadcrumb and surfaces inbound breadcrumbs via a callback (the
// host pins them into its SpaceServiceProtocol). Port of the C# reference
// (AetherNet.Space.SpaceBreadcrumbService).
//
// Wire payload: JSON, snake_case keys, field order
//   content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature
// created_at_ms is the UTC creation time as a bare Unix-ms integer (Int64), ttl_hours
// and type are bare integers (type = BreadcrumbType raw value: Notice=0, Emergency=1,
// Event=3), signature is STANDARD base64 (empty string when unsigned).
// Byte-identity gate: fixtures/space/vectors.json.

import Foundation

/// Binds ``PacketType/spaceBreadcrumb`` (40) to the mesh: broadcast a locally-dropped
/// breadcrumb, and surface inbound breadcrumbs via ``onBreadcrumbReceived`` (the host
/// pins them into its ``SpaceServiceProtocol``). Transport for the aether-space
/// geo-pinned-notice extension. Mirrors C# `SpaceBreadcrumbService`.
public actor SpaceBreadcrumbService {
    private let sender: any MeshSender

    /// Raised when a breadcrumb arrives from a peer.
    public var onBreadcrumbReceived: (@Sendable (SpaceBreadcrumb) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnBreadcrumbReceived(_ callback: (@Sendable (SpaceBreadcrumb) -> Void)?) {
        onBreadcrumbReceived = callback
    }

    /// Flood a breadcrumb to mesh peers. Returns the number of peers it was delivered to.
    @discardableResult
    public func broadcast(_ breadcrumb: SpaceBreadcrumb) async -> Int {
        let packet = MeshPacket(
            type: .spaceBreadcrumb,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: encodeSpaceBreadcrumbWire(breadcrumb)
        )
        return await sender.broadcast(packet)
    }

    /// Process an inbound ``PacketType/spaceBreadcrumb``. Fires ``onBreadcrumbReceived``.
    /// Returns false on the wrong packet type or a malformed / empty-content-hash payload.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .spaceBreadcrumb else { return false }
        guard let crumb = parseSpaceBreadcrumbWire(packet.payload), !crumb.contentHash.isEmpty else {
            return false
        }
        onBreadcrumbReceived?(crumb)
        return true
    }
}

// ─── SpaceBreadcrumb wire (PacketType 40) ───
//
// Foundation's JSONEncoder does NOT emit keys in a deterministic declaration order — with
// 3+ fields it hash-reorders them, breaking cross-language byte-identity. So the wire JSON
// is built BY HAND in the exact field order (content_hash, geo_hash, anchor_uhid,
// created_at_ms, ttl_hours, type, signature), mirroring the other language ports. Decode
// uses JSONDecoder (order-independent).

/// Decode-only mirror of the wire shape. Only used to parse inbound payloads.
private struct SpaceBreadcrumbWire: Codable {
    let content_hash: String
    let geo_hash: String
    let anchor_uhid: String
    let created_at_ms: Int64
    let ttl_hours: Int
    let type: Int
    let signature: String
    private enum CodingKeys: String, CodingKey {
        case content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature
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

/// Milliseconds since the Unix epoch for a `Date` (matches C# DateTimeOffset.ToUnixTimeMilliseconds).
private func unixMillis(_ date: Date) -> Int64 {
    Int64((date.timeIntervalSince1970 * 1000).rounded())
}

private func encodeSpaceBreadcrumbWire(_ b: SpaceBreadcrumb) -> Data {
    // STANDARD base64; empty signature → "" (Data().base64EncodedString() already yields "").
    let sig = b.signature.base64EncodedString()
    let json = "{\"content_hash\":\(jsonEscaped(b.contentHash)),"
        + "\"geo_hash\":\(jsonEscaped(b.geoHash)),"
        + "\"anchor_uhid\":\(jsonEscaped(b.anchorUhid)),"
        + "\"created_at_ms\":\(unixMillis(b.createdAtUtc)),"
        + "\"ttl_hours\":\(b.ttlHours),"
        + "\"type\":\(Int(b.type.rawValue)),"
        + "\"signature\":\(jsonEscaped(sig))}"
    return Data(json.utf8)
}

private func parseSpaceBreadcrumbWire(_ data: Data) -> SpaceBreadcrumb? {
    guard let w = try? JSONDecoder().decode(SpaceBreadcrumbWire.self, from: data) else { return nil }
    let signature = Data(base64Encoded: w.signature) ?? Data()
    return SpaceBreadcrumb(
        contentHash: w.content_hash,
        geoHash: w.geo_hash,
        anchorUhid: w.anchor_uhid,
        createdAtUtc: Date(timeIntervalSince1970: Double(w.created_at_ms) / 1000.0),
        ttlHours: w.ttl_hours,
        type: BreadcrumbType(rawValue: UInt8(truncatingIfNeeded: w.type)) ?? .notice,
        signature: signature
    )
}

/// Test-only shim exposing the real ``SpaceBreadcrumb`` wire encoder (the encoder stays
/// `private`) so byte-identity vectors in `fixtures/space/vectors.json` can be verified.
internal func _spaceBreadcrumbWireBytesForTests(_ b: SpaceBreadcrumb) -> Data {
    encodeSpaceBreadcrumbWire(b)
}
