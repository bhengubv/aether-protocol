// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherMeshProtocol

final class HandshakeServiceTests: XCTestCase {
    // MARK: - Helpers

    /// Builds a Hello/HelloAck packet with the given payload, addressed from
    /// `source` to `dest`. Mirrors what a peer node would put on the wire.
    private func makeHandshakePacket(
        type: PacketType,
        source: String,
        dest: String,
        payload: HelloPayload
    ) throws -> MeshPacket {
        let bytes = try payload.toJsonBytes()
        return MeshPacket(
            type: type,
            sourceUhid: source,
            destinationUhid: dest,
            ttl: 1,
            priority: 0,
            payload: bytes,
            protocolVersion: payload.maxVersion
        )
    }

    // MARK: - Tests

    func testInitiateSendsHelloPacketOnce() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        sender.addPeer(PeerInfo(uhid: "bob"))
        let service = HandshakeService(sender: sender)

        await service.initiate(peerUhid: "bob")

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        XCTAssertEqual(unicasts[0].packet.type, .hello)
        XCTAssertEqual(unicasts[0].packet.sourceUhid, "alice")

        // Second call to initiate should be a no-op (suppression).
        await service.initiate(peerUhid: "bob")
        XCTAssertEqual(sender.unicasts().count, 1, "Duplicate Hello must be suppressed")
    }

    func testInitiateDoesNotHelloSelf() async {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        await service.initiate(peerUhid: "alice")
        XCTAssertTrue(sender.unicasts().isEmpty)
    }

    func testHelloPayloadJsonRoundTrip() throws {
        let payload = HelloPayload(
            minVersion: 1,
            maxVersion: 2,
            capabilities: ["signal-x3dh", "double-ratchet"],
            implementation: "aether-swift/1.0.0"
        )
        let bytes = try payload.toJsonBytes()
        let str = String(decoding: bytes, as: UTF8.self)

        // Snake_case keys MUST be on the wire — interop with C# depends on it.
        XCTAssertTrue(str.contains("\"min_version\""), "Expected snake_case key min_version, got: \(str)")
        XCTAssertTrue(str.contains("\"max_version\""))
        XCTAssertTrue(str.contains("\"capabilities\""))
        XCTAssertTrue(str.contains("\"implementation\""))

        let restored = HelloPayload.fromJsonBytes(bytes)
        XCTAssertEqual(restored, payload)
    }

    func testHelloPayloadDecodesSnakeCase() {
        let json = #"{"min_version":1,"max_version":2,"capabilities":["sos"],"implementation":"aether-csharp/1.0.0"}"#
        let payload = HelloPayload.fromJsonBytes(json.data(using: .utf8)!)
        XCTAssertNotNil(payload)
        XCTAssertEqual(payload?.minVersion, 1)
        XCTAssertEqual(payload?.maxVersion, 2)
        XCTAssertEqual(payload?.capabilities, ["sos"])
        XCTAssertEqual(payload?.implementation, "aether-csharp/1.0.0")
    }

    func testHelloPayloadIgnoresExtraFields() {
        let json = #"{"min_version":1,"max_version":2,"extra":"junk"}"#
        let payload = HelloPayload.fromJsonBytes(json.data(using: .utf8)!)
        XCTAssertNotNil(payload, "Extra fields must be tolerated for forward-compat")
        XCTAssertEqual(payload?.minVersion, 1)
        XCTAssertEqual(payload?.maxVersion, 2)
        XCTAssertEqual(payload?.capabilities, [])
        XCTAssertEqual(payload?.implementation, "")
    }

    func testHandleHelloRepliesWithHelloAckAndNegotiates() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        sender.addPeer(PeerInfo(uhid: "bob"))
        let service = HandshakeService(sender: sender)

        let theirs = HelloPayload(
            minVersion: 1,
            maxVersion: 2,
            capabilities: ["signal-x3dh", "voice", "unknown-cap"],
            implementation: "aether-csharp/1.0.0"
        )
        let helloPkt = try makeHandshakePacket(
            type: .hello, source: "bob", dest: "alice", payload: theirs
        )

        try await service.handleHello(helloPkt)

        // HelloAck reply.
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .helloAck)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")

        // Negotiation locked in.
        let caps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertNotNil(caps)
        XCTAssertEqual(caps?.peerUhid, "bob")
        // min(ourMax=2, theirMax=2) = 2
        XCTAssertEqual(caps?.negotiatedVersion, 2)
        // Intersection: ours has signal-x3dh + voice; theirs has those plus
        // "unknown-cap" which we don't claim, so it must be excluded.
        XCTAssertEqual(caps?.capabilities, ["signal-x3dh", "voice"])
        XCTAssertEqual(caps?.implementationVersion, "aether-csharp/1.0.0")
    }

    func testVersionSelectionPicksHighestMutuallySupported() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(
            sender: sender,
            ourMinVersion: 1,
            ourMaxVersion: 5
        )

        let theirs = HelloPayload(minVersion: 2, maxVersion: 3, capabilities: [])
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )

        try await service.handleHelloAck(pkt)
        let caps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertEqual(caps?.negotiatedVersion, 3, "min(ourMax=5, theirMax=3) = 3")
    }

    func testCapabilityIntersection() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(
            sender: sender,
            ourCapabilities: ["a", "b", "c"]
        )
        let theirs = HelloPayload(
            minVersion: 1,
            maxVersion: 2,
            capabilities: ["b", "c", "d"]
        )
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )

        try await service.handleHelloAck(pkt)
        let caps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertEqual(caps?.capabilities, ["b", "c"])
    }

    func testDuplicateHelloSuppressed() async {
        let sender = FakeMeshSender(localUhid: "alice")
        sender.addPeer(PeerInfo(uhid: "bob"))
        let service = HandshakeService(sender: sender)

        await service.initiate(peerUhid: "bob")
        await service.initiate(peerUhid: "bob")
        await service.initiate(peerUhid: "bob")

        XCTAssertEqual(sender.unicasts().count, 1, "Repeated initiate must emit only one Hello")
    }

    func testIncompatiblePeerOnNoOverlap() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(
            sender: sender,
            ourMinVersion: 5,
            ourMaxVersion: 7
        )

        let captured = IncompatibleCapture()
        await service.addIncompatiblePeerListener { event in
            captured.set(event)
        }

        let theirs = HelloPayload(minVersion: 1, maxVersion: 3, capabilities: [])
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )

        try await service.handleHelloAck(pkt)

        let incompatibleCaps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertNil(incompatibleCaps, "No record should be stored for an incompatible peer")
        let event = captured.get()
        XCTAssertNotNil(event)
        XCTAssertEqual(event?.peerUhid, "bob")
        XCTAssertEqual(event?.theirMinVersion, 1)
        XCTAssertEqual(event?.theirMaxVersion, 3)
        XCTAssertEqual(event?.ourMinVersion, 5)
        XCTAssertEqual(event?.ourMaxVersion, 7)
    }

    func testIncompatiblePeerOnInvertedRange() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        let captured = IncompatibleCapture()
        await service.addIncompatiblePeerListener { event in
            captured.set(event)
        }

        // Inverted: min > max.
        let theirs = HelloPayload(minVersion: 5, maxVersion: 2, capabilities: [])
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )

        try await service.handleHelloAck(pkt)
        let event = captured.get()
        XCTAssertNotNil(event)
        XCTAssertTrue(event?.reason.contains("inverted") ?? false)
    }

    func testAssumeLegacyV1ForUnrespondedPeer() async {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        await service.assumeLegacyV1(peerUhid: "bob")
        let caps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertEqual(caps?.negotiatedVersion, 1)
        XCTAssertEqual(caps?.capabilities, [])
        XCTAssertEqual(caps?.implementationVersion, "")
    }

    func testAssumeLegacyV1IsIdempotent() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        // Real handshake first.
        let theirs = HelloPayload(
            minVersion: 1, maxVersion: 2,
            capabilities: ["signal-x3dh"],
            implementation: "aether-csharp/1.0.0"
        )
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )
        try await service.handleHelloAck(pkt)

        // Late legacy assumption — should NOT clobber the real record.
        await service.assumeLegacyV1(peerUhid: "bob")

        let caps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertEqual(caps?.negotiatedVersion, 2)
        XCTAssertEqual(caps?.capabilities, ["signal-x3dh"])
    }

    func testRenegotiateClearsState() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        sender.addPeer(PeerInfo(uhid: "bob"))
        let service = HandshakeService(sender: sender)

        await service.initiate(peerUhid: "bob")
        XCTAssertEqual(sender.unicasts().count, 1)

        await service.renegotiate(peerUhid: "bob")

        // After renegotiate, a fresh initiate should send another Hello.
        await service.initiate(peerUhid: "bob")
        XCTAssertEqual(sender.unicasts().count, 2)
    }

    func testHandleHelloRejectsWrongPacketType() async {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)
        let pkt = MeshPacket(type: .data, sourceUhid: "bob", destinationUhid: "alice")
        do {
            try await service.handleHello(pkt)
            XCTFail("Expected HandshakeError.unexpectedPacketType")
        } catch let HandshakeError.unexpectedPacketType(expected, actual) {
            XCTAssertEqual(expected, .hello)
            XCTAssertEqual(actual, .data)
        } catch {
            XCTFail("Wrong error: \(error)")
        }
    }

    func testNegotiatedListenerFiresOnHelloAck() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        let captured = NegotiatedCapture()
        await service.addPeerNegotiatedListener { caps in
            captured.set(caps)
        }

        let theirs = HelloPayload(minVersion: 1, maxVersion: 2, capabilities: ["sos"])
        let pkt = try makeHandshakePacket(
            type: .helloAck, source: "bob", dest: "alice", payload: theirs
        )
        try await service.handleHelloAck(pkt)

        let caps = captured.get()
        XCTAssertNotNil(caps)
        XCTAssertEqual(caps?.peerUhid, "bob")
    }

    func testGetAllNegotiatedSnapshot() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        for peer in ["bob", "carol", "dave"] {
            let theirs = HelloPayload(minVersion: 1, maxVersion: 2, capabilities: [])
            let pkt = try makeHandshakePacket(
                type: .helloAck, source: peer, dest: "alice", payload: theirs
            )
            try await service.handleHelloAck(pkt)
        }

        let all = await service.getAllNegotiated()
        XCTAssertEqual(all.count, 3)
        XCTAssertEqual(Set(all.map { $0.peerUhid }), ["bob", "carol", "dave"])
    }

    func testHelloPacketTypeRawValue() {
        // Wire interop: PacketType.hello MUST be 50 and helloAck 51 to match
        // the C# spec and every other-language port.
        XCTAssertEqual(PacketType.hello.rawValue, 50)
        XCTAssertEqual(PacketType.helloAck.rawValue, 51)
    }

    func testMalformedPayloadIgnored() async throws {
        let sender = FakeMeshSender(localUhid: "alice")
        let service = HandshakeService(sender: sender)

        let pkt = MeshPacket(
            type: .helloAck,
            sourceUhid: "bob",
            destinationUhid: "alice",
            payload: Data([0x00, 0x01, 0x02]) // not JSON
        )
        try await service.handleHelloAck(pkt)

        // Nothing locked in — the malformed payload was dropped silently.
        let malformedCaps = await service.getPeerCapabilities(peerUhid: "bob")
        XCTAssertNil(malformedCaps)
    }
}

// MARK: - Test capture helpers

/// Thread-safe single-slot capture for incompatible-peer events.
private final class IncompatibleCapture: @unchecked Sendable {
    private let lock = NSLock()
    private var event: IncompatiblePeerEvent?

    func set(_ e: IncompatiblePeerEvent) {
        lock.lock(); defer { lock.unlock() }
        event = e
    }
    func get() -> IncompatiblePeerEvent? {
        lock.lock(); defer { lock.unlock() }
        return event
    }
}

/// Thread-safe single-slot capture for peer-negotiated events.
private final class NegotiatedCapture: @unchecked Sendable {
    private let lock = NSLock()
    private var caps: PeerCapabilities?

    func set(_ c: PeerCapabilities) {
        lock.lock(); defer { lock.unlock() }
        caps = c
    }
    func get() -> PeerCapabilities? {
        lock.lock(); defer { lock.unlock() }
        return caps
    }
}
