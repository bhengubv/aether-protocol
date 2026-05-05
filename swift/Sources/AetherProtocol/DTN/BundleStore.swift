// SPDX-License-Identifier: MIT

import Foundation

/// Persistent backing store for DTN bundles + custody records.
public protocol BundleStore: Sendable {
    func get(_ bundleId: UUID) async -> DtnBundle?
    func getActive() async -> [DtnBundle]
    func save(_ bundle: DtnBundle) async
    func remove(_ bundleId: UUID) async
    func getActiveCount() async -> Int
    func saveCustody(_ record: CustodyRecord) async
    func getCustodyRecords(_ bundleId: UUID) async -> [CustodyRecord]
    func expireStale() async -> Int
}

/// Process-local DTN store. Suitable for tests.
public actor InMemoryBundleStore: BundleStore {
    private var bundles: [UUID: DtnBundle] = [:]
    private var custody: [UUID: CustodyRecord] = [:]

    public init() {}

    public func get(_ bundleId: UUID) -> DtnBundle? { bundles[bundleId] }

    public func getActive() -> [DtnBundle] {
        bundles.values.filter { b in
            !b.isExpired && (b.status == BundleStatus.pending.rawValue || b.status == BundleStatus.inCustody.rawValue)
        }
    }

    public func save(_ bundle: DtnBundle) {
        bundles[bundle.id] = bundle
    }

    public func remove(_ bundleId: UUID) {
        bundles.removeValue(forKey: bundleId)
    }

    public func getActiveCount() -> Int {
        bundles.values.filter { b in
            !b.isExpired && (b.status == BundleStatus.pending.rawValue || b.status == BundleStatus.inCustody.rawValue)
        }.count
    }

    public func saveCustody(_ record: CustodyRecord) {
        custody[record.id] = record
    }

    public func getCustodyRecords(_ bundleId: UUID) -> [CustodyRecord] {
        custody.values.filter { $0.bundleId == bundleId }
    }

    public func expireStale() -> Int {
        var expired = 0
        for (id, b) in bundles where b.isExpired && b.status != BundleStatus.expired.rawValue {
            bundles[id] = DtnBundle(
                id: b.id,
                senderUhid: b.senderUhid,
                recipientUhid: b.recipientUhid,
                encryptedPayload: b.encryptedPayload,
                priority: b.priority,
                status: BundleStatus.expired.rawValue,
                copyCount: b.copyCount,
                maxCopies: b.maxCopies,
                senderGeohash: b.senderGeohash,
                recipientLastGeohash: b.recipientLastGeohash,
                hopCount: b.hopCount,
                createdAt: b.createdAt,
                expiresAt: b.expiresAt
            )
            expired += 1
        }
        return expired
    }
}
