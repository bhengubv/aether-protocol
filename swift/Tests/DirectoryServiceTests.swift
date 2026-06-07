// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

/// Tests for ``DirectoryService`` — application-layer name resolution added in
/// v1.2.0 (Issue #60). Mirrors the C# DirectoryServiceTests structure.
final class DirectoryServiceTests: XCTestCase {

    private func sampleDescriptor(rootHash: String = "deadbeef") -> ContentDescriptor {
        ContentDescriptor(
            rootHash: rootHash,
            name: "ignored-publisher-hint",
            totalBytes: 1024,
            chunkSizeBytes: 256,
            chunkCount: 4,
            chunkHashes: ["h0", "h1", "h2", "h3"],
            contentType: "audio/flac"
        )
    }

    // MARK: - publish

    func test_publish_storesLocallyAndBroadcastsNamePublish() async throws {
        let sender = FakeMeshSender(localUhid: "publisher")
        sender.addPeer(PeerInfo(uhid: "peer-1", capabilities: 0))
        sender.addPeer(PeerInfo(uhid: "peer-2", capabilities: 0))
        let dir = DirectoryService(sender: sender)

        try await dir.publish(name: "podcast:abc", descriptor: sampleDescriptor(rootHash: "root-abc"))

        // Local resolve hits the catalogue immediately.
        let hit = await dir.resolve(name: "podcast:abc")
        XCTAssertNotNil(hit)
        XCTAssertEqual(hit?.rootHash, "root-abc")

        // Broadcast went out.
        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        XCTAssertEqual(broadcasts.first?.type, .namePublish)
    }

    func test_resolve_localCatalogueHit_returnsImmediately_noQueryBroadcast() async throws {
        let sender = FakeMeshSender(localUhid: "local")
        sender.addPeer(PeerInfo(uhid: "peer-1", capabilities: 0))
        let dir = DirectoryService(sender: sender)

        try await dir.publish(name: "track:xyz", descriptor: sampleDescriptor(rootHash: "root-xyz"))
        sender.clear()

        let hit = await dir.resolve(name: "track:xyz")

        XCTAssertNotNil(hit)
        XCTAssertEqual(hit?.rootHash, "root-xyz")
        XCTAssertTrue(sender.broadcasts().isEmpty, "no NameQuery should be sent on local catalogue hit")
    }

    // MARK: - Inbound NamePublish

    func test_handle_inboundNamePublish_populatesCatalogueAndFiresEvent() async throws {
        let sender = FakeMeshSender(localUhid: "local")
        let dir = DirectoryService(sender: sender)

        actor Box {
            var captured: DirectoryEntryAnnouncedEvent?
            func set(_ ev: DirectoryEntryAnnouncedEvent) { captured = ev }
        }
        let box = Box()
        await dir.setOnEntryAnnounced { ev in
            Task { await box.set(ev) }
        }

        // Build a NamePublish packet from a peer.
        let descriptor = sampleDescriptor(rootHash: "from-peer")
        let payload = NamePublishPayload(name: "reel:hello", descriptor: descriptor, inResponseToQueryId: nil)
        let body = try JSONEncoder().encode(payload)
        let packet = MeshPacket(
            type: .namePublish,
            sourceUhid: "peer-publisher",
            payload: body
        )
        try await dir.handle(packet)

        // Allow callback Task to flush.
        try? await Task.sleep(nanoseconds: 50_000_000)

        // Local catalogue now has the entry.
        let hit = await dir.resolve(name: "reel:hello")
        XCTAssertNotNil(hit)
        XCTAssertEqual(hit?.rootHash, "from-peer")

        // Event fired.
        let captured = await box.captured
        XCTAssertNotNil(captured)
        XCTAssertEqual(captured?.name, "reel:hello")
        XCTAssertEqual(captured?.sourceUhid, "peer-publisher")
        XCTAssertEqual(captured?.descriptor.rootHash, "from-peer")
    }

    // MARK: - Query / Response roundtrip

    func test_handle_queryWithMatchingName_unicastsNamePublishResponse() async throws {
        let holderSender = FakeMeshSender(localUhid: "holder")
        holderSender.addPeer(PeerInfo(uhid: "asker", capabilities: 0))
        let holder = DirectoryService(sender: holderSender)

        try await holder.publish(name: "album:test", descriptor: sampleDescriptor(rootHash: "album-root"))
        holderSender.clear()

        // Build a NameQuery as if from `asker`.
        let queryId = UUID()
        let queryPayload = NameQueryPayload(name: "album:test", queryId: queryId)
        let queryBody = try JSONEncoder().encode(queryPayload)
        let queryPacket = MeshPacket(
            type: .nameQuery,
            sourceUhid: "asker",
            payload: queryBody
        )

        try await holder.handle(queryPacket)

        // Holder unicasts back a NamePublish with inResponseToQueryId set.
        let unicasts = holderSender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        guard let response = unicasts.first else { return }
        XCTAssertEqual(response.nextHopUhid, "asker")
        XCTAssertEqual(response.packet.type, .namePublish)

        let decoded = try JSONDecoder().decode(NamePublishPayload.self, from: response.packet.payload)
        XCTAssertEqual(decoded.name, "album:test")
        XCTAssertEqual(decoded.descriptor.rootHash, "album-root")
        XCTAssertEqual(decoded.inResponseToQueryId, queryId)
    }

    func test_handle_queryForUnknownName_doesNothing() async throws {
        let sender = FakeMeshSender(localUhid: "local")
        sender.addPeer(PeerInfo(uhid: "asker", capabilities: 0))
        let dir = DirectoryService(sender: sender)

        let queryPayload = NameQueryPayload(name: "nothing-here", queryId: UUID())
        let queryBody = try JSONEncoder().encode(queryPayload)
        let queryPacket = MeshPacket(
            type: .nameQuery,
            sourceUhid: "asker",
            payload: queryBody
        )

        try await dir.handle(queryPacket)

        XCTAssertTrue(sender.unicasts().isEmpty)
        XCTAssertTrue(sender.broadcasts().isEmpty)
    }

    func test_resolve_missAndTimeout_returnsNil() async {
        let sender = FakeMeshSender(localUhid: "local")
        sender.addPeer(PeerInfo(uhid: "peer-1", capabilities: 0))
        let dir = DirectoryService(sender: sender)

        let hit = await dir.resolve(name: "unknown-name", timeout: 0.05)

        XCTAssertNil(hit)
        // A NameQuery WAS broadcast — we tried.
        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        XCTAssertEqual(broadcasts.first?.type, .nameQuery)
    }

    func test_resolve_queryAndAnswerArrives_returnsDescriptor() async throws {
        let sender = FakeMeshSender(localUhid: "local")
        sender.addPeer(PeerInfo(uhid: "peer-1", capabilities: 0))
        let dir = DirectoryService(sender: sender)

        // Kick off resolve in the background.
        async let resolveResult = dir.resolve(name: "podcast:remote", timeout: 2.0)

        // Wait briefly for the NameQuery to be broadcast.
        try? await Task.sleep(nanoseconds: 100_000_000)

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        guard let queryBroadcast = broadcasts.first else { return }
        XCTAssertEqual(queryBroadcast.type, .nameQuery)

        let queryDecoded = try JSONDecoder().decode(NameQueryPayload.self, from: queryBroadcast.payload)

        // Simulate a peer responding with a NamePublish carrying inResponseToQueryId.
        let descriptor = sampleDescriptor(rootHash: "remote-root")
        let response = NamePublishPayload(
            name: "podcast:remote",
            descriptor: descriptor,
            inResponseToQueryId: queryDecoded.queryId
        )
        let responseBody = try JSONEncoder().encode(response)
        let responsePacket = MeshPacket(
            type: .namePublish,
            sourceUhid: "peer-1",
            payload: responseBody
        )
        try await dir.handle(responsePacket)

        let result = await resolveResult
        XCTAssertNotNil(result)
        XCTAssertEqual(result?.rootHash, "remote-root")
    }

    // MARK: - Listing

    func test_listNames_returnsCatalogueSnapshot() async throws {
        let sender = FakeMeshSender(localUhid: "local")
        let dir = DirectoryService(sender: sender)

        try await dir.publish(name: "a", descriptor: sampleDescriptor(rootHash: "hash-a"))
        try await dir.publish(name: "b", descriptor: sampleDescriptor(rootHash: "hash-b"))
        try await dir.publish(name: "c", descriptor: sampleDescriptor(rootHash: "hash-c"))

        let names = await dir.listNames()

        XCTAssertEqual(names.count, 3)
        XCTAssertTrue(names.contains("a"))
        XCTAssertTrue(names.contains("b"))
        XCTAssertTrue(names.contains("c"))
    }
}
