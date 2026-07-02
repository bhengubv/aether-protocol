// SPDX-License-Identifier: MIT

// Wire binding for PacketType.vaultShardRequest (42) — the transport half of the
// aether-vault erasure-coded-storage extension. A node broadcasts this to ask the
// mesh for an erasure-coded shard it needs to recover a file; a holder answers from
// its VaultServiceProtocol. Port of the C# reference
// (AetherNet.Vault.VaultShardRequestService).
//
// Wire payload: JSON, snake_case keys, field order
//   shard_hash, requester_uhid
// Byte-identity gate: fixtures/vaultshard/vectors.json.

import Foundation

/// Event surfaced when a peer requests a shard that this node may hold.
/// Mirrors the C# `VaultShardRequest` event args.
public struct VaultShardRequest: Sendable, Equatable {
    /// The shard hash being requested.
    public let shardHash: String
    /// UHID of the requesting peer.
    public let requesterUhid: String

    public init(shardHash: String, requesterUhid: String) {
        self.shardHash = shardHash
        self.requesterUhid = requesterUhid
    }
}

/// Binds ``PacketType/vaultShardRequest`` (42) to the mesh: ask peers for a shard, and
/// surface inbound shard requests via ``onShardRequested`` (the host answers from its
/// ``VaultServiceProtocol`` if it holds the shard). Transport for the aether-vault
/// erasure-coded-storage extension. Mirrors C# `VaultShardRequestService`.
public actor VaultShardRequestService {
    private let sender: any MeshSender

    /// Raised when a peer requests a shard.
    public var onShardRequested: (@Sendable (VaultShardRequest) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnShardRequested(_ callback: (@Sendable (VaultShardRequest) -> Void)?) {
        onShardRequested = callback
    }

    /// Broadcast a request for `shardHash` (requester = this node's UHID). Returns the
    /// number of peers reached. No-op (returns 0) for an empty shard hash.
    @discardableResult
    public func requestShard(_ shardHash: String) async -> Int {
        guard !shardHash.isEmpty else { return 0 }
        let packet = MeshPacket(
            type: .vaultShardRequest,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: encodeVaultShardRequestWire(shardHash: shardHash, requesterUhid: sender.localUhid)
        )
        return await sender.broadcast(packet)
    }

    /// Process an inbound ``PacketType/vaultShardRequest``. Fires ``onShardRequested``.
    /// Returns false on the wrong packet type or a malformed / empty-shard-hash payload.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .vaultShardRequest else { return false }
        guard let req = parseVaultShardRequestWire(packet.payload), !req.shardHash.isEmpty else {
            return false
        }
        onShardRequested?(req)
        return true
    }
}

// ─── VaultShardRequest wire (PacketType 42) ───
//
// Hand-built in exact field order (shard_hash, requester_uhid) so cross-language
// byte-identity does not depend on JSONEncoder key ordering. Decode uses JSONDecoder
// (order-independent).

private struct VaultShardRequestWire: Codable {
    let shard_hash: String
    let requester_uhid: String
    private enum CodingKeys: String, CodingKey {
        case shard_hash, requester_uhid
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

private func encodeVaultShardRequestWire(shardHash: String, requesterUhid: String) -> Data {
    let json = "{\"shard_hash\":\(jsonEscaped(shardHash)),"
        + "\"requester_uhid\":\(jsonEscaped(requesterUhid))}"
    return Data(json.utf8)
}

private func parseVaultShardRequestWire(_ data: Data) -> VaultShardRequest? {
    guard let w = try? JSONDecoder().decode(VaultShardRequestWire.self, from: data) else { return nil }
    return VaultShardRequest(shardHash: w.shard_hash, requesterUhid: w.requester_uhid)
}

/// Test-only shim exposing the real ``VaultShardRequest`` wire encoder (the encoder stays
/// `private`) so byte-identity vectors in `fixtures/vaultshard/vectors.json` can be verified.
internal func _vaultShardRequestWireBytesForTests(shardHash: String, requesterUhid: String) -> Data {
    encodeVaultShardRequestWire(shardHash: shardHash, requesterUhid: requesterUhid)
}
