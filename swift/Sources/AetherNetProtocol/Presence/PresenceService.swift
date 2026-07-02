// SPDX-License-Identifier: MIT

import Foundation

// ─── PresenceBeaconReceived / PresenceQueryReceived ───────────────────────

/// Event surfaced when a presence beacon arrives from a peer.
///
/// Carries the peer's ROTATING ``erid`` (Ephemeral Routing Id — never the stable UHID), a COARSE
/// ``geohash`` (host-truncated; empty when hidden), its ``capabilities`` bitmask, a presence
/// ``status``, and the send timestamp. Mirrors C# `PresenceBeaconReceived`.
public struct PresenceBeaconReceived: Sendable, Equatable {
    /// The node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID.
    public let erid: String
    /// Coarse geohash of the node (host-truncated per privacy level); empty string = hidden.
    public let geohash: String
    /// NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …).
    public let capabilities: Int
    /// PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5).
    public let status: Int
    /// Unix timestamp (ms) when the beacon was sent.
    public let sentAtMs: Int64
    /// UHID of the peer that sent the beacon.
    public let fromUhid: String

    public init(erid: String, geohash: String, capabilities: Int, status: Int, sentAtMs: Int64, fromUhid: String) {
        self.erid = erid
        self.geohash = geohash
        self.capabilities = capabilities
        self.status = status
        self.sentAtMs = sentAtMs
        self.fromUhid = fromUhid
    }
}

/// Event surfaced when a presence query ("who's around here?") arrives from a peer.
///
/// Mirrors C# `PresenceQueryReceived`.
public struct PresenceQueryReceived: Sendable, Equatable {
    /// Id of the query (minted by the querier).
    public let queryId: UUID
    /// Coarse geohash the query is scoped to; empty = "anywhere".
    public let geohash: String
    /// UHID of the peer that sent the query.
    public let fromUhid: String

    public init(queryId: UUID, geohash: String, fromUhid: String) {
        self.queryId = queryId
        self.geohash = geohash
        self.fromUhid = fromUhid
    }
}

// ─── PresenceService ──────────────────────────────────────────────────────

/// Presence over ``PacketType/presenceBeacon`` (PacketType 21) and ``PacketType/presenceQuery``
/// (PacketType 22) — a privacy-preserving "I'm here" broadcast plus a "who's around here?" query.
///
/// Broadcast a beacon (the host builds it with the rotating erid + coarse geohash), broadcast a
/// query for a (possibly empty) geohash, and surface inbound beacons/queries via
/// ``onBeaconReceived`` / ``onQueryReceived``. Transport only — the ERID rotation and geohash
/// coarsening are the host's concern; this service never touches the stable UHID or precise
/// location.
///
/// Mirrors C# `PresenceService`.
public actor PresenceService {
    private let sender: any MeshSender

    /// Raised when a presence beacon arrives from a peer.
    public var onBeaconReceived: (@Sendable (PresenceBeaconReceived) -> Void)?
    /// Raised when a presence query arrives from a peer.
    public var onQueryReceived: (@Sendable (PresenceQueryReceived) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnBeaconReceived(_ callback: (@Sendable (PresenceBeaconReceived) -> Void)?) {
        onBeaconReceived = callback
    }

    public func setOnQueryReceived(_ callback: (@Sendable (PresenceQueryReceived) -> Void)?) {
        onQueryReceived = callback
    }

    // MARK: – Broadcast

    /// Broadcast a presence beacon carrying the (already-rotated) `erid` + (already-coarse)
    /// `geohash`. Returns the number of peers reached directly.
    @discardableResult
    public func broadcastBeacon(
        erid: String,
        geohash: String,
        capabilities: Int,
        status: Int,
        sentAtMs: Int64
    ) async -> Int {
        let body = encodePresenceBeaconWire(
            erid: erid,
            geohash: geohash,
            capabilities: capabilities,
            status: status,
            sentAtMs: sentAtMs
        )
        let packet = MeshPacket(
            type: .presenceBeacon,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )
        return await sender.broadcast(packet)
    }

    /// Broadcast a presence query for the given (coarse, possibly empty) `geohash`. Mints and
    /// returns the new query id.
    @discardableResult
    public func query(_ geohash: String) async -> UUID {
        let queryId = UUID()
        let body = encodePresenceQueryWire(queryId: queryId, geohash: geohash)
        let packet = MeshPacket(
            type: .presenceQuery,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )
        _ = await sender.broadcast(packet)
        return queryId
    }

    // MARK: – Inbound dispatch

    /// Process an incoming presence packet (beacon or query): parse and fire the matching event.
    /// Returns false for the wrong packet type, a malformed payload, or a beacon whose `erid` is
    /// empty; true once the event has been surfaced.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        switch packet.type {
        case .presenceBeacon:
            guard let b = parsePresenceBeaconWire(packet.payload), !b.erid.isEmpty else { return false }
            onBeaconReceived?(PresenceBeaconReceived(
                erid: b.erid,
                geohash: b.geohash,
                capabilities: b.capabilities,
                status: b.status,
                sentAtMs: b.sentAtMs,
                fromUhid: packet.sourceUhid
            ))
            return true

        case .presenceQuery:
            guard let q = parsePresenceQueryWire(packet.payload) else { return false }
            onQueryReceived?(PresenceQueryReceived(
                queryId: q.queryId,
                geohash: q.geohash,
                fromUhid: packet.sourceUhid
            ))
            return true

        default:
            return false
        }
    }
}

// ─── Presence wire (PacketType 21 / 22) ───
//
// Beacon(21): snake_case keys, field order erid, geohash, capabilities, status, sent_at_ms, no
// whitespace, geohash may be "", capabilities/status/sent_at_ms bare integers. Query(22): field
// order query_id, geohash, GUID lowercase-dashed. These are the byte-identity gate
// (fixtures/presence/vectors.json).

private struct PresenceBeaconWire: Codable {
    let erid: String
    let geohash: String
    let capabilities: Int
    let status: Int
    let sent_at_ms: Int64
    // Lock the wire field order explicitly (erid, geohash, capabilities, status, sent_at_ms) —
    // matches the convention used by the other wrapped wire structs so the byte-identity gate
    // never depends on Codable's synthesis order. Decode is order-independent regardless.
    private enum CodingKeys: String, CodingKey {
        case erid, geohash, capabilities, status, sent_at_ms
    }
}

private struct PresenceQueryWire: Codable {
    @LowercaseUUIDCoding var query_id: UUID
    let geohash: String
    private enum CodingKeys: String, CodingKey {
        case query_id, geohash
    }
}

// Foundation's JSONEncoder does NOT emit keys in a deterministic declaration order — with 3+
// fields it hash-reorders them, breaking cross-language byte-identity. So the wire JSON is built
// by hand in the exact field order, mirroring the other language ports (and the Swift
// ChannelMessageService / VideoCallControlService). Decode still uses JSONDecoder below, which is
// order-independent.
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

private func encodePresenceBeaconWire(
    erid: String,
    geohash: String,
    capabilities: Int,
    status: Int,
    sentAtMs: Int64
) -> Data {
    let json = "{\"erid\":\(jsonEscaped(erid)),"
        + "\"geohash\":\(jsonEscaped(geohash)),"
        + "\"capabilities\":\(capabilities),"
        + "\"status\":\(status),"
        + "\"sent_at_ms\":\(sentAtMs)}"
    return Data(json.utf8)
}

private func encodePresenceQueryWire(queryId: UUID, geohash: String) -> Data {
    let json = "{\"query_id\":\"\(queryId.uuidString.lowercased())\","
        + "\"geohash\":\(jsonEscaped(geohash))}"
    return Data(json.utf8)
}

private func parsePresenceBeaconWire(
    _ data: Data
) -> (erid: String, geohash: String, capabilities: Int, status: Int, sentAtMs: Int64)? {
    guard let w = try? JSONDecoder().decode(PresenceBeaconWire.self, from: data) else { return nil }
    return (w.erid, w.geohash, w.capabilities, w.status, w.sent_at_ms)
}

private func parsePresenceQueryWire(_ data: Data) -> (queryId: UUID, geohash: String)? {
    guard let w = try? JSONDecoder().decode(PresenceQueryWire.self, from: data) else { return nil }
    return (w.query_id, w.geohash)
}

/// Test-only shims exposing the real presence wire serialization path (the wire structs stay
/// `private`) so the byte-identity vectors in `fixtures/presence/vectors.json` can be verified.
internal func _presenceBeaconWireBytesForTests(
    erid: String,
    geohash: String,
    capabilities: Int,
    status: Int,
    sentAtMs: Int64
) -> Data {
    encodePresenceBeaconWire(
        erid: erid,
        geohash: geohash,
        capabilities: capabilities,
        status: status,
        sentAtMs: sentAtMs
    )
}

internal func _presenceQueryWireBytesForTests(queryId: UUID, geohash: String) -> Data {
    encodePresenceQueryWire(queryId: queryId, geohash: geohash)
}
