// SPDX-License-Identifier: MIT

// In-memory aether-vault service (Phase-2 extension): erasure-coded distributed
// backup over this module's ReedSolomonCodec. Port of the C# reference
// (AetherNet.Vault.InMemoryVaultService) — K=10 / M=4, shard layout byte-identical
// so a shard set produced here is decodable by any other node.

import Crypto
import Foundation

/// Data shards in the default vault scheme.
public let vaultK = 10
/// Parity shards in the default vault scheme.
public let vaultM = 4

/// The only thing the owner must retain to reconstruct a vaulted file.
public struct VaultManifest {
    public var contentHash: String     // SHA-256 hex of the plaintext
    public var shardHashes: [String]   // SHA-256 hex of each of the K+M shards
    public var k: Int
    public var m: Int
    public var sizeBytes: Int64
    public var label: String
    public var createdAtUtc: Date

    public init(
        contentHash: String = "",
        shardHashes: [String] = [],
        k: Int = vaultK,
        m: Int = vaultM,
        sizeBytes: Int64 = 0,
        label: String = "",
        createdAtUtc: Date = Date()
    ) {
        self.contentHash = contentHash
        self.shardHashes = shardHashes
        self.k = k
        self.m = m
        self.sizeBytes = sizeBytes
        self.label = label
        self.createdAtUtc = createdAtUtc
    }

    /// Total shards for this manifest (K + M).
    public var totalShards: Int { k + m }
}

/// A current reachability report for a vaulted file.
public struct VaultHealth {
    public var totalShards: Int
    public var reachableShards: Int
    public var isRecoverable: Bool
    public var redundancyScore: Double

    public init(totalShards: Int = 0, reachableShards: Int = 0, isRecoverable: Bool = false, redundancyScore: Double = 0) {
        self.totalShards = totalShards
        self.reachableShards = reachableShards
        self.isRecoverable = isRecoverable
        self.redundancyScore = redundancyScore
    }
}

/// The aether-vault erasure-coded backup store.
public protocol VaultServiceProtocol {
    func store(data: [UInt8], label: String) async throws -> VaultManifest
    func recover(manifest: VaultManifest) async throws -> [UInt8]
    func checkHealth(manifest: VaultManifest) -> VaultHealth
    func replicate(manifest: VaultManifest, targetRedundancy: Int) async throws
}

/// In-memory `VaultServiceProtocol` for testing / single-node use; shards lost on deinit.
public final class InMemoryVaultService: VaultServiceProtocol {
    private var shards: [String: [UInt8]] = [:] // shard hash -> bytes

    public init() {}

    private func sha256Hex(_ data: [UInt8]) -> String {
        SHA256.hash(data: Data(data)).map { String(format: "%02x", $0) }.joined()
    }

    public func store(data: [UInt8], label: String) async throws -> VaultManifest {
        let contentHash = sha256Hex(data)
        let codec = try ReedSolomonCodec(k: vaultK, m: vaultM)

        let shardArr: [[UInt8]]
        if data.isEmpty {
            // Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
            shardArr = try codec.encode(Array(repeating: [UInt8](repeating: 0, count: 1), count: vaultK))
        } else {
            shardArr = try codec.encodeData(data)
        }

        var shardHashes: [String] = []
        shardHashes.reserveCapacity(shardArr.count)
        for sh in shardArr {
            let h = sha256Hex(sh)
            shards[h] = sh
            shardHashes.append(h)
        }

        return VaultManifest(
            contentHash: contentHash,
            shardHashes: shardHashes,
            k: vaultK,
            m: vaultM,
            sizeBytes: Int64(data.count),
            label: label,
            createdAtUtc: Date()
        )
    }

    public func recover(manifest: VaultManifest) async throws -> [UInt8] {
        let total = manifest.shardHashes.count
        let k = manifest.k
        let m = total - k
        let codec = try ReedSolomonCodec(k: k, m: m)

        var available: [Int: [UInt8]] = [:]
        for (i, h) in manifest.shardHashes.enumerated() {
            if let sh = shards[h] { available[i] = sh }
        }
        if available.count < k {
            throw ReedSolomonError.unrecoverable("vault: cannot recover — only \(available.count)/\(k) shards available")
        }
        return try codec.reconstructData(available, originalSize: Int(manifest.sizeBytes))
    }

    public func checkHealth(manifest: VaultManifest) -> VaultHealth {
        let reachable = manifest.shardHashes.reduce(0) { $0 + (shards[$1] != nil ? 1 : 0) }
        let total = manifest.totalShards
        return VaultHealth(
            totalShards: total,
            reachableShards: reachable,
            isRecoverable: reachable >= manifest.k,
            redundancyScore: total > 0 ? Double(reachable) / Double(total) : 0
        )
    }

    public func replicate(manifest: VaultManifest, targetRedundancy: Int = 14) async throws {
        // No-op in the in-memory implementation.
    }
}
