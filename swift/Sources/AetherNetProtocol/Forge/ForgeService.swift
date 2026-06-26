// SPDX-License-Identifier: MIT

// aether-forge: a mesh-native package cache proxy (Phase-2 extension). The first
// internet pull of a package is cached as Aether content; subsequent pulls by
// anyone in the mesh are served locally at mesh speeds. Port of the C# reference
// (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.

import Foundation

/// Metadata record for one cached package artifact.
public struct ForgeEntry {
    public var contentHash: String
    public var packageId: String
    public var fetchedAtUtc: Date
    public var sizeBytes: Int64
    public var downloadCount: Int

    public init(
        contentHash: String = "",
        packageId: String = "",
        fetchedAtUtc: Date = Date(),
        sizeBytes: Int64 = 0,
        downloadCount: Int = 0
    ) {
        self.contentHash = contentHash
        self.packageId = packageId
        self.fetchedAtUtc = fetchedAtUtc
        self.sizeBytes = sizeBytes
        self.downloadCount = downloadCount
    }
}

/// Aggregate statistics for the local Forge cache.
public struct ForgeStats {
    public var totalBytesSaved: Int64
    public var totalPeersServed: Int
    public var catalogueSize: Int
    public var topPackages: [ForgeEntry]

    public init(totalBytesSaved: Int64 = 0, totalPeersServed: Int = 0, catalogueSize: Int = 0, topPackages: [ForgeEntry] = []) {
        self.totalBytesSaved = totalBytesSaved
        self.totalPeersServed = totalPeersServed
        self.catalogueSize = catalogueSize
        self.topPackages = topPackages
    }
}

/// The mesh-native package cache.
public protocol ForgeServiceProtocol {
    func query(packageId: String) async -> ForgeEntry?
    func cache(packageId: String, contentHash: String, sizeBytes: Int64) async -> ForgeEntry
    func fetch(packageId: String) async -> ForgeEntry?
    func getStats() async -> ForgeStats
}

/// In-memory `ForgeServiceProtocol` for testing / single-node use; state lost on deinit.
public final class InMemoryForgeService: ForgeServiceProtocol {
    private var store: [String: ForgeEntry] = [:] // key = packageId

    /// Fires when a new artifact is added via cache().
    public var onNewEntryAnnounced: ((ForgeEntry) -> Void)?

    public init() {}

    public func query(packageId: String) async -> ForgeEntry? {
        store[packageId]
    }

    public func cache(packageId: String, contentHash: String, sizeBytes: Int64) async -> ForgeEntry {
        if let existing = store[packageId] {
            return existing // idempotent — first write wins
        }
        let entry = ForgeEntry(
            contentHash: contentHash,
            packageId: packageId,
            fetchedAtUtc: Date(),
            sizeBytes: sizeBytes,
            downloadCount: 0
        )
        store[packageId] = entry
        onNewEntryAnnounced?(entry)
        return entry
    }

    public func fetch(packageId: String) async -> ForgeEntry? {
        guard store[packageId] != nil else { return nil }
        store[packageId]!.downloadCount += 1
        return store[packageId]
    }

    public func getStats() async -> ForgeStats {
        let entries = Array(store.values)
        let totalBytesSaved = entries.reduce(Int64(0)) { $0 + Int64($1.downloadCount) * $1.sizeBytes }
        let topPackages = entries.sorted { $0.downloadCount > $1.downloadCount }.prefix(10)
        return ForgeStats(
            totalBytesSaved: totalBytesSaved,
            totalPeersServed: 0,
            catalogueSize: entries.count,
            topPackages: Array(topPackages)
        )
    }
}
