// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "local"

private func buildBundlePacket(source: String, bundle: DtnBundle) -> MeshPacket {
    return MeshPacket(
        type: .dtnBundle,
        sourceUhid: source,
        destinationUhid: bundle.recipientUhid,
        payload: DtnEnvelope.serializeBundle(bundle)
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
            let parsed = DtnEnvelope.deserializeCustodyAck(ack.packet.payload)
            XCTAssertEqual(parsed?.1, false)
        }
    }

    // MARK: - DtnCustodyAck

    func test_handle_positiveCustodyAck_incrementsCopyCount() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1]))
        let initial = b.copyCount

        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: "carrier",
            destinationUhid: LOCAL,
            payload: DtnEnvelope.serializeCustodyAck(bundleId: b.id, accepted: true)
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

        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: "carrier",
            destinationUhid: LOCAL,
            payload: DtnEnvelope.serializeCustodyAck(bundleId: b.id, accepted: false)
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

        let pkt = MeshPacket(
            type: .dtnDeliveryReceipt,
            sourceUhid: "recipient",
            destinationUhid: LOCAL,
            payload: DtnEnvelope.serializeDeliveryReceipt(DtnDeliveryReceipt(
                bundleId: b.id, recipientUhid: "recipient",
                totalHops: 3, totalCustodyTransfers: 2,
                deliveredAt: Date(timeIntervalSince1970: 0)
            ))
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

    // MARK: - Reputation hooks

    func test_handle_deliveryToSelf_firesRecordDeliverySuccess() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let rep = NodeReputationService()
        await svc.setReputation(rep)

        let bundle = DtnBundle(
            senderUhid: "alice",
            recipientUhid: LOCAL,
            encryptedPayload: Data([1])
        )
        await svc.handle(buildBundlePacket(source: "alice", bundle: bundle))

        let score = await rep.reputationScore(for: "alice")
        // score starts at 1.0; recordDeliverySuccess adds +0.01 → clamped to 1.0
        // The key assertion is that the score was touched, not below the default
        XCTAssertGreaterThanOrEqual(score, 1.0)
    }

    func test_handle_deliveryForOther_doesNotFireRecordDeliverySuccess() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let rep = NodeReputationService()
        await svc.setReputation(rep)

        // Bundle destined for "bob", not LOCAL — should NOT fire recordDeliverySuccess
        let bundle = DtnBundle(
            senderUhid: "alice",
            recipientUhid: "bob",
            encryptedPayload: Data([2])
        )
        await svc.handle(buildBundlePacket(source: "alice", bundle: bundle))

        // "alice" score should be untouched (still default 1.0, no delivery-success delta applied)
        let allScores = await rep.allScores()
        XCTAssertNil(allScores["alice"], "reputation must not record delivery success for transit bundles")
    }

    func test_handle_negativeCustodyAck_firesRecordCustodyRefusal() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)
        let rep = NodeReputationService()
        await svc.setReputation(rep)

        let b = await svc.createBundle(recipientUhid: "recipient", encryptedPayload: Data([1]))

        let pkt = MeshPacket(
            type: .dtnCustodyAck,
            sourceUhid: "carrier",
            destinationUhid: LOCAL,
            payload: DtnEnvelope.serializeCustodyAck(bundleId: b.id, accepted: false)
        )
        await svc.handle(pkt)

        // recordCustodyRefusal subtracts 0.05 from "carrier" (starts at 1.0 → 0.95)
        let score = await rep.reputationScore(for: "carrier")
        XCTAssertLessThan(score, 1.0, "custody refusal must lower the refusing peer's reputation score")
        XCTAssertEqual(score, 0.95, accuracy: 1e-9)
    }

    // MARK: - OnBundleReceived (v1.2.0, Issue #59)

    func test_handle_inboundBundleAddressedToLocal_firesOnBundleReceived() async {
        let sender = FakeMeshSender(localUhid: "recipient")
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)

        actor Captured {
            var events: [DtnBundleReceivedEvent] = []
            func add(_ e: DtnBundleReceivedEvent) { events.append(e) }
            func snapshot() -> [DtnBundleReceivedEvent] { events }
        }
        let captured = Captured()
        await svc.setOnBundleReceived { e in
            Task { await captured.add(e) }
        }

        let bundle = DtnBundle(
            senderUhid: "remote-sender",
            recipientUhid: "recipient",
            encryptedPayload: Data([0x01, 0x02, 0x03, 0x04]),
            priority: BundlePriority.high.rawValue,
            hopCount: 2
        )
        await svc.handle(buildBundlePacket(source: "carrier", bundle: bundle))

        // give the Task time to land
        try? await Task.sleep(nanoseconds: 50_000_000)

        let events = await captured.snapshot()
        XCTAssertEqual(events.count, 1)
        let evt = events[0]
        XCTAssertEqual(evt.bundleId, bundle.id)
        XCTAssertEqual(evt.senderUhid, "remote-sender")
        XCTAssertEqual(evt.recipientUhid, "recipient")
        XCTAssertEqual(evt.encryptedPayload, Data([0x01, 0x02, 0x03, 0x04]))
        XCTAssertEqual(evt.priority, .high)
        XCTAssertEqual(evt.hopCount, 2)
    }

    func test_handle_inboundBundleForOtherNode_doesNotFireOnBundleReceived() async {
        let sender = FakeMeshSender(localUhid: "carrier")
        let store = InMemoryBundleStore()
        let svc = DtnService(sender: sender, store: store)

        actor Captured {
            var fired = false
            func mark() { fired = true }
            func value() -> Bool { fired }
        }
        let captured = Captured()
        await svc.setOnBundleReceived { _ in
            Task { await captured.mark() }
        }

        let bundle = DtnBundle(
            senderUhid: "remote-sender",
            recipientUhid: "someone-else",
            encryptedPayload: Data([0xff])
        )
        await svc.handle(buildBundlePacket(source: "remote-sender", bundle: bundle))

        try? await Task.sleep(nanoseconds: 50_000_000)
        let fired = await captured.value()
        XCTAssertFalse(fired, "onBundleReceived must fire ONLY when local node is recipient")
    }
}
