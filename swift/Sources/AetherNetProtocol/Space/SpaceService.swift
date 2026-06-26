// SPDX-License-Identifier: MIT

// aether-space: geo-pinned community noticeboards (Phase-2 extension). Nodes drop
// breadcrumbs at geohash coordinates; passing devices auto-pull and re-host them
// for other passersby — fully offline. Port of the C# reference (AetherNet.Space).
// Wire format: JSON, transmitted as PacketType.spaceBreadcrumb (40).

import Foundation

/// Category of a geo-pinned breadcrumb.
public enum BreadcrumbType: UInt8 {
    case notice = 0
    case emergency = 1
    case commerce = 2
    case event = 3
    case jobPosting = 4
}

public let spaceEmergencyTtlHours = 720
public let spaceMinTtlHours = 1
public let spaceMaxTtlHours = 168

/// A geo-pinned digital notice dropped at a physical location. Content is
/// addressed by hash; the breadcrumb carries only metadata.
public struct SpaceBreadcrumb {
    public var contentHash: String
    public var geoHash: String
    public var anchorUhid: String
    public var createdAtUtc: Date
    public var ttlHours: Int
    public var type: BreadcrumbType
    public var signature: Data

    public init(
        contentHash: String = "",
        geoHash: String = "",
        anchorUhid: String = "",
        createdAtUtc: Date = Date(),
        ttlHours: Int = 72,
        type: BreadcrumbType = .notice,
        signature: Data = Data()
    ) {
        self.contentHash = contentHash
        self.geoHash = geoHash
        self.anchorUhid = anchorUhid
        self.createdAtUtc = createdAtUtc
        self.ttlHours = ttlHours
        self.type = type
        self.signature = signature
    }

    /// UTC expiry = createdAtUtc + ttlHours.
    public var expiresAtUtc: Date {
        createdAtUtc.addingTimeInterval(Double(ttlHours) * 3600)
    }

    /// True once the breadcrumb's TTL has passed.
    public var isExpired: Bool {
        Date() >= expiresAtUtc
    }
}

/// The aether-space breadcrumb store.
public protocol SpaceServiceProtocol {
    func drop(geoHash: String, contentHash: String, anchorUhid: String, type: BreadcrumbType, ttlHours: Int) async -> SpaceBreadcrumb
    func scan(centerGeoHash: String, radiusCells: Int) async -> [SpaceBreadcrumb]
    func pin(_ breadcrumb: SpaceBreadcrumb) async
    func delete(_ breadcrumb: SpaceBreadcrumb, requestorUhid: String) async -> Bool
    func pruneExpired() -> Int
}

private func clampInt(_ value: Int, _ lo: Int, _ hi: Int) -> Int {
    return min(max(value, lo), hi)
}

/// In-memory `SpaceServiceProtocol` for testing / single-node use; state lost on
/// deinit. Proximity matching uses a geohash-prefix heuristic.
public final class InMemorySpaceService: SpaceServiceProtocol {
    private var store: [String: SpaceBreadcrumb] = [:] // key = contentHash

    /// Fires when a breadcrumb is dropped locally or pinned from the mesh.
    public var onBreadcrumbReceived: ((SpaceBreadcrumb) -> Void)?
    /// Fires when a cached breadcrumb passes its TTL.
    public var onBreadcrumbExpired: ((SpaceBreadcrumb) -> Void)?

    public init() {}

    public func drop(
        geoHash: String,
        contentHash: String,
        anchorUhid: String,
        type: BreadcrumbType = .notice,
        ttlHours: Int = 72
    ) async -> SpaceBreadcrumb {
        let effectiveTtl = type == .emergency
            ? spaceEmergencyTtlHours
            : clampInt(ttlHours, spaceMinTtlHours, spaceMaxTtlHours)
        let crumb = SpaceBreadcrumb(
            contentHash: contentHash,
            geoHash: geoHash,
            anchorUhid: anchorUhid,
            createdAtUtc: Date(),
            ttlHours: effectiveTtl,
            type: type
        )
        store[contentHash] = crumb
        onBreadcrumbReceived?(crumb)
        return crumb
    }

    public func scan(centerGeoHash: String, radiusCells: Int = 1) async -> [SpaceBreadcrumb] {
        // Prefix-based proximity: match the first (6 - radiusCells) chars.
        let prefixLen = clampInt(6 - radiusCells, 1, 6)
        let prefix = (centerGeoHash.count >= prefixLen
            ? String(centerGeoHash.prefix(prefixLen))
            : centerGeoHash).lowercased()
        return store.values.filter { !$0.isExpired && $0.geoHash.lowercased().hasPrefix(prefix) }
    }

    public func pin(_ breadcrumb: SpaceBreadcrumb) async {
        store[breadcrumb.contentHash] = breadcrumb
        onBreadcrumbReceived?(breadcrumb)
    }

    public func delete(_ breadcrumb: SpaceBreadcrumb, requestorUhid: String) async -> Bool {
        guard let stored = store[breadcrumb.contentHash] else { return false }
        guard stored.anchorUhid == requestorUhid else { return false } // creator-only delete
        store.removeValue(forKey: breadcrumb.contentHash)
        return true
    }

    public func pruneExpired() -> Int {
        let expired = store.values.filter { $0.isExpired }
        for crumb in expired {
            store.removeValue(forKey: crumb.contentHash)
            onBreadcrumbExpired?(crumb)
        }
        return expired.count
    }
}
