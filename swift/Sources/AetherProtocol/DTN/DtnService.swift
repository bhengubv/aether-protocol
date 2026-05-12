// SPDX-License-Identifier: MIT

import Foundation

/// Default DTN service. Three-tier delivery:
///   direct mesh send → DTN epidemic replication → backend relay.
public actor DtnService {
    private let sender: any MeshSender
    private let store: any BundleStore
    private let strategy: any ReplicationStrategy
    private let incentives: any IncentiveProvider
    private let backend: any BackendClient
    private var reputation: NodeReputationService?

    public var onBundleDelivered: (@Sendable (DtnDeliveryReceipt) -> Void)?

    public init(
        sender: any MeshSender,
        store: any BundleStore = InMemoryBundleStore(),
        strategy: any ReplicationStrategy = GeohashEpidemicStrategy(),
        incentives: any IncentiveProvider = NoopIncentiveProvider(),
        backend: any BackendClient = NoopBackendClient()
    ) {
        self.sender = sender
        self.store = store
        self.strategy = strategy
        self.incentives = incentives
        self.backend = backend
    }

    public func setReputation(_ rep: NodeReputationService?) {
        self.reputation = rep
    }

    public func setOnBundleDelivered(_ callback: (@Sendable (DtnDeliveryReceipt) -> Void)?) {
        self.onBundleDelivered = callback
    }

    public func createBundle(
        recipientUhid: String,
        encryptedPayload: Data,
        priority: BundlePriority = .normal,
        recipientLastGeohash: String? = nil
    ) async -> DtnBundle {
        var bundle = DtnBundle(
            senderUhid: sender.localUhid,
            recipientUhid: recipientUhid,
            encryptedPayload: encryptedPayload,
            priority: priority.rawValue,
            status: BundleStatus.pending.rawValue,
            senderGeohash: sender.localGeohash,
            recipientLastGeohash: recipientLastGeohash
        )
        await store.save(bundle)

        if await tryDirectDelivery(bundle) {
            bundle = withStatus(bundle, status: .delivered)
            await store.save(bundle)
        }
        return bundle
    }

    public func handle(_ packet: MeshPacket) async {
        switch packet.type {
        case .dtnBundle: await handleBundle(packet)
        case .dtnCustodyAck: await handleCustodyAck(packet)
        case .dtnDeliveryReceipt: await handleDeliveryReceipt(packet)
        default: break
        }
    }

    public func runDeliveryScan() async {
        let active = await store.getActive()
        if active.isEmpty { return }
        let peers = sender.connectedPeers()
        let localGeohash = sender.localGeohash

        for var bundle in active {
            if bundle.status == BundleStatus.delivered.rawValue || bundle.isExpired { continue }
            if await tryDirectDelivery(bundle) {
                bundle = withStatus(bundle, status: .delivered)
                await store.save(bundle)
                continue
            }
            if peers.isEmpty || bundle.copyCount >= bundle.maxCopies { continue }
            let targets = strategy.selectTargets(bundle: bundle, peers: peers, localGeohash: localGeohash)
            for target in targets {
                if bundle.copyCount >= bundle.maxCopies { break }
                let pkt = bundlePacket(bundle)
                if await sender.send(pkt, nextHopUhid: target) {
                    bundle = withCopyCount(bundle, copyCount: bundle.copyCount + 1)
                    await store.save(bundle)
                    await incentives.recordRelay(localUhid: sender.localUhid, packet: pkt)
                }
            }
        }
    }

    public func expireStale() async -> Int { await store.expireStale() }
    public func getActiveBundles() async -> [DtnBundle] { await store.getActive() }

    private func tryDirectDelivery(_ bundle: DtnBundle) async -> Bool {
        let pkt = bundlePacket(bundle)
        for peer in sender.connectedPeers() where peer.uhid == bundle.recipientUhid {
            if await sender.send(pkt, nextHopUhid: bundle.recipientUhid) { return true }
            break
        }
        return await backend.syncDtnBundle(bundle)
    }

    private func bundlePacket(_ bundle: DtnBundle) -> MeshPacket {
        MeshPacket(
            id: bundle.id,
            type: .dtnBundle,
            sourceUhid: sender.localUhid,
            destinationUhid: bundle.recipientUhid,
            ttl: 30,
            priority: UInt8(clamping: bundle.priority),
            payload: encodeBundle(bundle)
        )
    }

    private func handleBundle(_ packet: MeshPacket) async {
        guard let bundle = decodeBundle(packet.payload) else { return }
        if bundle.recipientUhid == sender.localUhid {
            let delivered = withStatus(bundle, status: .delivered)
            await store.save(delivered)
            await sendDeliveryReceipt(delivered)
            await reputation?.recordDeliverySuccess(uhid: packet.sourceUhid, roundTripMs: 0)
            return
        }
        if await store.getActiveCount() >= ProtocolConstants.dtnMaxBundlesPerNode {
            await sendCustodyAck(bundleId: bundle.id, toUhid: packet.sourceUhid, accepted: false)
            return
        }
        let inCustody = withStatus(withHopCount(bundle, hopCount: bundle.hopCount + 1), status: .inCustody)
        await store.save(inCustody)
        await store.saveCustody(CustodyRecord(
            bundleId: bundle.id,
            fromUhid: packet.sourceUhid,
            toUhid: sender.localUhid,
            accepted: true
        ))
        await sendCustodyAck(bundleId: bundle.id, toUhid: packet.sourceUhid, accepted: true)
        await incentives.recordRelay(localUhid: sender.localUhid, packet: packet)
    }

    private func handleCustodyAck(_ packet: MeshPacket) async {
        guard let (bundleId, accepted) = parseCustodyAck(packet.payload) else { return }
        if !accepted {
            await reputation?.recordCustodyRefusal(uhid: packet.sourceUhid)
            return
        }
        guard let b = await store.get(bundleId) else { return }
        await store.save(withCopyCount(b, copyCount: b.copyCount + 1))
    }

    private func handleDeliveryReceipt(_ packet: MeshPacket) async {
        guard let receipt = parseDeliveryReceipt(packet.payload) else { return }
        if let b = await store.get(receipt.bundleId) {
            await store.save(withStatus(b, status: .delivered))
        }
        if let cb = onBundleDelivered { cb(receipt) }
    }

    private func sendCustodyAck(bundleId: UUID, toUhid: String, accepted: Bool) async {
        if toUhid.isEmpty { return }
        let payload = encodeCustodyAck(bundleId: bundleId, accepted: accepted)
        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: sender.localUhid,
            destinationUhid: toUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: payload
        )
        _ = await sender.send(pkt, nextHopUhid: toUhid)
    }

    private func sendDeliveryReceipt(_ bundle: DtnBundle) async {
        if bundle.senderUhid.isEmpty || bundle.senderUhid == sender.localUhid { return }
        let custody = await store.getCustodyRecords(bundle.id)
        let receipt = DtnDeliveryReceipt(
            bundleId: bundle.id,
            recipientUhid: bundle.recipientUhid,
            totalHops: bundle.hopCount,
            totalCustodyTransfers: Int32(custody.count),
            deliveredAt: Date()
        )
        let pkt = MeshPacket(
            type: .dtnDeliveryReceipt,
            sourceUhid: sender.localUhid,
            destinationUhid: bundle.senderUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: encodeDeliveryReceipt(receipt)
        )
        _ = await sender.send(pkt, nextHopUhid: bundle.senderUhid)
    }

    // ─── DtnBundle is immutable; copy with field changes ───
    private func withStatus(_ b: DtnBundle, status: BundleStatus) -> DtnBundle {
        DtnBundle(
            id: b.id, senderUhid: b.senderUhid, recipientUhid: b.recipientUhid,
            encryptedPayload: b.encryptedPayload, priority: b.priority,
            status: status.rawValue, copyCount: b.copyCount, maxCopies: b.maxCopies,
            senderGeohash: b.senderGeohash, recipientLastGeohash: b.recipientLastGeohash,
            hopCount: b.hopCount, createdAt: b.createdAt, expiresAt: b.expiresAt
        )
    }

    private func withCopyCount(_ b: DtnBundle, copyCount: Int32) -> DtnBundle {
        DtnBundle(
            id: b.id, senderUhid: b.senderUhid, recipientUhid: b.recipientUhid,
            encryptedPayload: b.encryptedPayload, priority: b.priority,
            status: b.status, copyCount: copyCount, maxCopies: b.maxCopies,
            senderGeohash: b.senderGeohash, recipientLastGeohash: b.recipientLastGeohash,
            hopCount: b.hopCount, createdAt: b.createdAt, expiresAt: b.expiresAt
        )
    }

    private func withHopCount(_ b: DtnBundle, hopCount: Int32) -> DtnBundle {
        DtnBundle(
            id: b.id, senderUhid: b.senderUhid, recipientUhid: b.recipientUhid,
            encryptedPayload: b.encryptedPayload, priority: b.priority,
            status: b.status, copyCount: b.copyCount, maxCopies: b.maxCopies,
            senderGeohash: b.senderGeohash, recipientLastGeohash: b.recipientLastGeohash,
            hopCount: hopCount, createdAt: b.createdAt, expiresAt: b.expiresAt
        )
    }
}

// ─── snake_case JSON wire helpers ───

private struct BundleWire: Codable {
    let id: UUID
    let sender_uhid: String
    let recipient_uhid: String
    let encrypted_payload: Data
    let priority: Int32
    let status: Int32
    let copy_count: Int32
    let max_copies: Int32
    let sender_geohash: String?
    let recipient_last_geohash: String?
    let hop_count: Int32
    let created_at_ms: Int64
    let expires_at_ms: Int64
}

private struct CustodyAckWire: Codable {
    let bundle_id: UUID
    let accepted: Bool
}

private struct DeliveryReceiptWire: Codable {
    let bundle_id: UUID
    let recipient_uhid: String
    let total_hops: Int32
    let total_custody_transfers: Int32
    let delivered_at_ms: Int64
}

private func encodeBundle(_ b: DtnBundle) -> Data {
    let wire = BundleWire(
        id: b.id,
        sender_uhid: b.senderUhid,
        recipient_uhid: b.recipientUhid,
        encrypted_payload: b.encryptedPayload,
        priority: b.priority,
        status: b.status,
        copy_count: b.copyCount,
        max_copies: b.maxCopies,
        sender_geohash: b.senderGeohash,
        recipient_last_geohash: b.recipientLastGeohash,
        hop_count: b.hopCount,
        created_at_ms: Int64(b.createdAt.timeIntervalSince1970 * 1000),
        expires_at_ms: Int64(b.expiresAt.timeIntervalSince1970 * 1000)
    )
    return (try? JSONEncoder().encode(wire)) ?? Data()
}

private func decodeBundle(_ data: Data) -> DtnBundle? {
    guard let wire = try? JSONDecoder().decode(BundleWire.self, from: data) else { return nil }
    return DtnBundle(
        id: wire.id,
        senderUhid: wire.sender_uhid,
        recipientUhid: wire.recipient_uhid,
        encryptedPayload: wire.encrypted_payload,
        priority: wire.priority,
        status: wire.status,
        copyCount: wire.copy_count,
        maxCopies: wire.max_copies,
        senderGeohash: wire.sender_geohash,
        recipientLastGeohash: wire.recipient_last_geohash,
        hopCount: wire.hop_count,
        createdAt: Date(timeIntervalSince1970: TimeInterval(wire.created_at_ms) / 1000),
        expiresAt: Date(timeIntervalSince1970: TimeInterval(wire.expires_at_ms) / 1000)
    )
}

private func encodeCustodyAck(bundleId: UUID, accepted: Bool) -> Data {
    let w = CustodyAckWire(bundle_id: bundleId, accepted: accepted)
    return (try? JSONEncoder().encode(w)) ?? Data()
}

private func parseCustodyAck(_ data: Data) -> (UUID, Bool)? {
    guard let w = try? JSONDecoder().decode(CustodyAckWire.self, from: data) else { return nil }
    return (w.bundle_id, w.accepted)
}

private func encodeDeliveryReceipt(_ r: DtnDeliveryReceipt) -> Data {
    let w = DeliveryReceiptWire(
        bundle_id: r.bundleId,
        recipient_uhid: r.recipientUhid,
        total_hops: r.totalHops,
        total_custody_transfers: r.totalCustodyTransfers,
        delivered_at_ms: Int64(r.deliveredAt.timeIntervalSince1970 * 1000)
    )
    return (try? JSONEncoder().encode(w)) ?? Data()
}

private func parseDeliveryReceipt(_ data: Data) -> DtnDeliveryReceipt? {
    guard let w = try? JSONDecoder().decode(DeliveryReceiptWire.self, from: data) else { return nil }
    return DtnDeliveryReceipt(
        bundleId: w.bundle_id,
        recipientUhid: w.recipient_uhid,
        totalHops: w.total_hops,
        totalCustodyTransfers: w.total_custody_transfers,
        deliveredAt: Date(timeIntervalSince1970: TimeInterval(w.delivered_at_ms) / 1000)
    )
}
