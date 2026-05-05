// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherProtocol

private let LOCAL = "local"

private struct BundleWireMirror: Codable {
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

private struct CustodyAckWireMirror: Codable {
    let bundle_id: UUID
    let accepted: Bool
}

private struct DeliveryReceiptWireMirror: Codable {
    let bundle_id: UUID
    let recipient_uhid: String
    let total_hops: Int32
    let total_custody_transfers: Int32
    let delivered_at_ms: Int64
}

private func buildBundlePacket(source: String, bundle: DtnBundle) -> MeshPacket {
    let wire = BundleWireMirror(
        id: bundle.id,
        sender_uhid: bundle.senderUhid,
        recipient_uhid: bundle.recipientUhid,
        encrypted_payload: bundle.encryptedPayload,
        priority: bundle.priority,
        status: bundle.status,
        copy_count: bundle.copyCount,
        max_copies: bundle.maxCopies,
        sender_geohash: bundle.senderGeohash,
        recipient_last_geohash: bundle.recipientLastGeohash,
        hop_count: bundle.hopCount,
        created_at_ms: Int64(bundle.createdAt.timeIntervalSince1970 * 1000),
        expires_at_ms: Int64(bundle.expiresAt.timeIntervalSince1970 * 1000)
    )
    let payload = (try? JSONEncoder().encode(wire)) ?? Data()
    return MeshPacket(
        type: .dtnBundle,
        sourceUhid: source,
        destinationUhid: bundle.recipientUhid,
        payload: payload
    )
}

final class DtnServiceTests: XCTestCase {

    // MARK: - CreateBundle

    func test_createBundle_persistsAndAttemptsDelivery() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1,2,3]))
        XCTAssertEqual(b.recipientUhid, "recipient")
        XCTAssertEqual(b.status, BundleStatus.pending.rawValue)
        let active = await store.getActive()
        XCTAssertEqual(active.count, 1)
    }

    func test_createBundle_withDirectPeer_deliversImmediately() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        sender.addPeer(PeerInfo(
            uhid: "recipient",
            capabilities: NodeCapabilityBits.dtnCarrier
        ))
        let svc = DtnService(sender: sender)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1,2,3]))
        XCTAssertEqual(b.status, BundleStatus.delivered.rawValue)
        XCTAssertTrue(sender.unicasts().contains(where: {
            $0.nextHopUhid == "recipient" && $0.packet.type == .dtnBundle
        }))
    }

    // MARK: - HandleAsync — DtnBundle

    func test_handle_asRecipient_marksDeliveredAndSendsReceipt() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)

        let bundle = DtnBundle(
            senderUhid: "alice",
            recipientUhid: LOCAL,
            encryptedPayload: Data([9])
        )
        await svc.handle(buildBundlePacket(source: "alice", bundle: bundle))

        let stored = await store.get(bundle.id)
        XCTAssertNotNil(stored)
        XCTAssertEqual(stored?.status, BundleStatus.delivered.rawValue)
        XCTAssertTrue(sender.unicasts().contains(where: {
            $0.packet.type == .dtnDeliveryReceipt && $0.nextHopUhid == "alice"
        }))
    }

    func test_handle_notRecipientWithCapacity_acceptsCustody() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)

        let bundle = DtnBundle(
            senderUhid: "alice",
            recipientUhid: "bob",
            encryptedPayload: Data([1])
        )
        await svc.handle(buildBundlePacket(source: "alice", bundle: bundle))

        let stored = await store.get(bundle.id)
        XCTAssertNotNil(stored)
        XCTAssertEqual(stored?.status, BundleStatus.inCustody.rawValue)
        XCTAssertEqual(stored?.hopCount, 1)
        XCTAssertTrue(sender.unicasts().contains(where: {
            $0.packet.type == .dtnCustodyAck && $0.nextHopUhid == "alice"
        }))
    }

    func test_handle_atCapacity_refusesCustody() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        for _ in 0..<ProtocolConstants.dtnMaxBundlesPerNode {
            let fill = DtnBundle(
                senderUhid: "x", recipientUhid: "y",
                encryptedPayload: Data(),
                status: BundleStatus.inCustody.rawValue
            )
            await store.save(fill)
        }
        sender.clear()

        let bundle = DtnBundle(
            senderUhid: "alice", recipientUhid: "bob", encryptedPayload: Data()
        )
        await svc.handle(buildBundlePacket(source: "alice", bundle: bundle))

        let ack = sender.unicasts().first(where: { $0.packet.type == .dtnCustodyAck })
        XCTAssertNotNil(ack)
        if let ack {
            let parsed = try? JSONDecoder().decode(CustodyAckWireMirror.self, from: ack.packet.payload)
            XCTAssertEqual(parsed?.accepted, false)
        }
    }

    // MARK: - DtnCustodyAck

    func test_handle_positiveCustodyAck_incrementsCopyCount() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1]))
        let initial = b.copyCount

        let body = try? JSONEncoder().encode(CustodyAckWireMirror(bundle_id: b.id, accepted: true))
        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: "carrier",
            destinationUhid: LOCAL,
            payload: body ?? Data()
        )
        await svc.handle(pkt)

        let stored = await store.get(b.id)
        XCTAssertEqual(stored?.copyCount, initial + 1)
    }

    func test_handle_negativeCustodyAck_doesNotIncrement() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1]))
        let initial = b.copyCount

        let body = try? JSONEncoder().encode(CustodyAckWireMirror(bundle_id: b.id, accepted: false))
        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: "carrier",
            destinationUhid: LOCAL,
            payload: body ?? Data()
        )
        await svc.handle(pkt)

        let stored = await store.get(b.id)
        XCTAssertEqual(stored?.copyCount, initial)
    }

    // MARK: - DtnDeliveryReceipt

    func test_handle_deliveryReceipt_marksBundleDelivered() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1]))

        let body = try? JSONEncoder().encode(DeliveryReceiptWireMirror(
            bundle_id: b.id, recipient_uhid: "recipient",
            total_hops: 3, total_custody_transfers: 2, delivered_at_ms: 0
        ))
        let pkt = MeshPacket(
            type: .dtnDeliveryReceipt,
            sourceUhid: "recipient",
            destinationUhid: LOCAL,
            payload: body ?? Data()
        )
        await svc.handle(pkt)

        let stored = await store.get(b.id)
        XCTAssertEqual(stored?.status, BundleStatus.delivered.rawValue)
    }

    // MARK: - ExpireStale

    func test_expireStale_flipsStatusForExpiredBundles() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)

        let expired = DtnBundle(
            senderUhid: "a", recipientUhid: "b",
            encryptedPayload: Data(),
            status: BundleStatus.pending.rawValue,
            expiresAt: Date(timeIntervalSinceNow: -60)
        )
        await store.save(expired)

        let fresh = DtnBundle(
            senderUhid: "a", recipientUhid: "b",
            encryptedPayload: Data(),
            status: BundleStatus.pending.rawValue
        )
        await store.save(fresh)

        let n = await svc.expireStale()
        XCTAssertEqual(n, 1)
        let storedFresh = await store.get(fresh.id)
        XCTAssertEqual(storedFresh?.status, BundleStatus.pending.rawValue)
    }
}
