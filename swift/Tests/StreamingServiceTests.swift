// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherMeshProtocol

private let LOCAL = "alice"

/// Build a StreamSubscribe packet from `subscriber` targeting a specific stream.
private func subscribePacket(subscriber: String, streamId: UUID) -> MeshPacket {
    let json = "{\"stream_id\":\"\(streamId.uuidString.lowercased())\",\"subscriber_uhid\":\"\(subscriber)\"}"
    var pkt = MeshPacket(type: .streamSubscribe, sourceUhid: subscriber, destinationUhid: LOCAL, priority: 32)
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

/// Build a StreamUnsubscribe packet.
private func unsubscribePacket(subscriber: String, streamId: UUID) -> MeshPacket {
    let json = "{\"stream_id\":\"\(streamId.uuidString.lowercased())\",\"subscriber_uhid\":\"\(subscriber)\"}"
    var pkt = MeshPacket(type: .streamUnsubscribe, sourceUhid: subscriber, destinationUhid: LOCAL, priority: 32)
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

/// Build a StreamAnnounce packet (inbound, from a remote publisher).
private func announcePacket(publisher: String, streamId: UUID, title: String) -> MeshPacket {
    let json = "{\"stream_id\":\"\(streamId.uuidString.lowercased())\",\"publisher_uhid\":\"\(publisher)\",\"title\":\"\(title)\",\"mime_type\":\"video/h264\"}"
    var pkt = MeshPacket(type: .streamAnnounce, sourceUhid: publisher, destinationUhid: LOCAL, priority: 32)
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

/// Build a StreamAnnounce "end" packet from a remote publisher.
private func endAnnouncePacket(publisher: String, streamId: UUID) -> MeshPacket {
    let json = "{\"stream_id\":\"\(streamId.uuidString.lowercased())\",\"publisher_uhid\":\"\(publisher)\",\"signal_type\":\"end\"}"
    var pkt = MeshPacket(type: .streamAnnounce, sourceUhid: publisher, destinationUhid: LOCAL, priority: 32)
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

/// Build a binary StreamSegment packet.
private func segmentPacket(streamId: UUID, sequence: UInt32 = 0, timestampMs: Int64 = 0, isKeyframe: Bool = false, audio: Data) -> MeshPacket {
    var buf = Data(count: 29 + audio.count)
    // [0..15] stream id
    let uuidBytes = withUnsafeBytes(of: streamId.uuid) { Data($0) }
    buf.replaceSubrange(0..<16, with: uuidBytes)
    // [16..19] sequence LE
    var seqLE = sequence.littleEndian
    withUnsafeBytes(of: seqLE) { buf.replaceSubrange(16..<20, with: $0) }
    // [20..27] timestamp LE
    var tsLE = timestampMs.littleEndian
    withUnsafeBytes(of: tsLE) { buf.replaceSubrange(20..<28, with: $0) }
    // [28] isKeyframe
    buf[28] = isKeyframe ? 1 : 0
    // [29..] audio
    buf.replaceSubrange(29..<(29 + audio.count), with: audio)

    var pkt = MeshPacket(type: .streamSegment, sourceUhid: "bob", destinationUhid: LOCAL, priority: 16)
    pkt.payload = buf
    return pkt
}

// ── Tests ──────────────────────────────────────────────────────────────────────

final class StreamingServiceTests: XCTestCase {

    // MARK: – startStream

    func test_startStream_returnsNonNilStreamId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "My Stream", mimeType: "video/h264")
        XCTAssertNotEqual(sid, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
    }

    func test_startStream_broadcastsStreamAnnounce() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        _ = try await svc.startStream(title: "Live", mimeType: "audio/opus")
        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 1)
        XCTAssertEqual(bcasts[0].type, .streamAnnounce)
    }

    func test_startStream_announcePayloadContainsStreamId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "Live", mimeType: "audio/opus")
        let body = String(data: sender.broadcasts()[0].payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains(sid.uuidString.lowercased()), "announce payload must contain stream id")
    }

    // MARK: – endStream

    func test_endStream_broadcastsEndAnnounce() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "Live", mimeType: "audio/opus")
        sender.clear()

        try await svc.endStream(streamId: sid)

        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 1)
        XCTAssertEqual(bcasts[0].type, .streamAnnounce)
        let body = String(data: bcasts[0].payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("end"), "end announce payload must contain signal_type=end")
    }

    func test_endStream_notifiesSubscribersDirectly() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "video/h264")
        // Bob subscribes via inbound packet so the publisher knows about him.
        try await svc.handlePacket(subscribePacket(subscriber: "bob", streamId: sid))
        sender.clear()

        try await svc.endStream(streamId: sid)

        let unicasts = sender.unicasts()
        XCTAssertTrue(unicasts.contains(where: { $0.nextHopUhid == "bob" }), "endStream must unicast end notice to known subscriber")
    }

    func test_endStream_firesOnStreamEnded() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "audio/opus")

        var endedId: UUID?
        await svc.setOnStreamEnded { id in endedId = id }

        try await svc.endStream(streamId: sid)
        XCTAssertEqual(endedId, sid)
    }

    // MARK: – subscribe / unsubscribe

    func test_subscribe_sendsStreamSubscribePacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = UUID()

        try await svc.subscribe(streamId: sid, publisherUhid: "bob")

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .streamSubscribe)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains(sid.uuidString.lowercased()), "subscribe payload must contain stream id")
    }

    func test_unsubscribe_sendsStreamUnsubscribePacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = UUID()

        try await svc.subscribe(streamId: sid, publisherUhid: "bob")
        sender.clear()
        try await svc.unsubscribe(streamId: sid, publisherUhid: "bob")

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .streamUnsubscribe)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
    }

    // MARK: – handlePacket — subscribe / unsubscribe

    func test_handleSubscribe_addSubscriberSoPublishReachesThem() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "video/h264")

        try await svc.handlePacket(subscribePacket(subscriber: "bob", streamId: sid))
        sender.clear()

        try await svc.publishSegment(streamId: sid, encodedData: Data([0x01, 0x02]), isKeyframe: false)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1, "segment should reach the one subscriber")
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        XCTAssertEqual(unicasts[0].packet.type, .streamSegment)
    }

    func test_handleUnsubscribe_removesSubscriberSoPublishSkipsThem() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "video/h264")

        try await svc.handlePacket(subscribePacket(subscriber: "bob", streamId: sid))
        try await svc.handlePacket(unsubscribePacket(subscriber: "bob", streamId: sid))
        sender.clear()

        try await svc.publishSegment(streamId: sid, encodedData: Data([0xAA]), isKeyframe: false)

        XCTAssertTrue(sender.unicasts().isEmpty, "no segment after unsubscribe")
    }

    // MARK: – publishSegment

    func test_publishSegment_fansOutToMultipleSubscribers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "audio/opus")

        try await svc.handlePacket(subscribePacket(subscriber: "bob", streamId: sid))
        try await svc.handlePacket(subscribePacket(subscriber: "carol", streamId: sid))
        sender.clear()

        try await svc.publishSegment(streamId: sid, encodedData: Data([0x01]), isKeyframe: false)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 2, "segment should reach both subscribers")
        let targets = Set(unicasts.map { $0.nextHopUhid })
        XCTAssertTrue(targets.contains("bob"))
        XCTAssertTrue(targets.contains("carol"))
    }

    func test_publishSegment_unknownStreamDoesNotSend() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)

        // No stream started — publishSegment should silently do nothing (stream not found).
        try await svc.publishSegment(streamId: UUID(), encodedData: Data([0x01]), isKeyframe: false)

        XCTAssertTrue(sender.unicasts().isEmpty, "publish on unknown stream must not send anything")
    }

    func test_publishSegment_payloadHasCorrectWireFormat() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)
        let sid = try await svc.startStream(title: "T", mimeType: "video/h264")
        try await svc.handlePacket(subscribePacket(subscriber: "bob", streamId: sid))
        sender.clear()

        let video = Data([0x11, 0x22, 0x33, 0x44])
        try await svc.publishSegment(streamId: sid, encodedData: video, isKeyframe: true)

        let pkt = sender.unicasts()[0].packet
        // Wire: [16 streamId][4 seq][8 ts][1 isKeyframe][N data]
        XCTAssertGreaterThanOrEqual(pkt.payload.count, 29 + video.count)
        // isKeyframe byte at offset 28 must be 1
        XCTAssertEqual(pkt.payload[28], 1, "isKeyframe flag must be set")
        // Last bytes must be our video data
        let n = pkt.payload.count
        XCTAssertEqual(pkt.payload[(n - video.count)...], video[...])
    }

    // MARK: – handlePacket — inbound announce

    func test_handlePacket_inboundAnnounce_firesOnStreamAnnounced() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)

        var announcedId: UUID?
        var announcedPublisher = ""
        await svc.setOnStreamAnnounced { sid, publisher, _ in
            announcedId = sid
            announcedPublisher = publisher
        }

        let sid = UUID()
        try await svc.handlePacket(announcePacket(publisher: "bob", streamId: sid, title: "Bob's Stream"))

        XCTAssertEqual(announcedId, sid)
        XCTAssertEqual(announcedPublisher, "bob")
    }

    // MARK: – handlePacket — inbound end announce

    func test_handlePacket_endAnnounce_firesOnStreamEnded() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)

        let sid = UUID()
        // First announce so the stream is tracked.
        try await svc.handlePacket(announcePacket(publisher: "bob", streamId: sid, title: "Bob's Stream"))

        var endedId: UUID?
        await svc.setOnStreamEnded { id in endedId = id }

        try await svc.handlePacket(endAnnouncePacket(publisher: "bob", streamId: sid))

        XCTAssertEqual(endedId, sid)
    }

    // MARK: – handlePacket — inbound segment

    func test_handlePacket_inboundSegment_firesOnSegmentReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)

        let sid = UUID()
        // Subscribe locally so the service accepts segments for this stream.
        try await svc.subscribe(streamId: sid, publisherUhid: "bob")

        var frameReceived = false
        var receivedData = Data()
        await svc.setOnSegmentReceived { _, data, _, _, _ in
            frameReceived = true
            receivedData = data
        }

        let audio = Data([0xAA, 0xBB, 0xCC])
        try await svc.handlePacket(segmentPacket(streamId: sid, isKeyframe: false, audio: audio))

        XCTAssertTrue(frameReceived, "onSegmentReceived must fire for subscribed stream")
        XCTAssertEqual(receivedData, audio)
    }

    func test_handlePacket_inboundSegment_notSubscribed_doesNotFire() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = StreamingService(sender: sender)

        var frameReceived = false
        await svc.setOnSegmentReceived { _, _, _, _, _ in frameReceived = true }

        // Do NOT subscribe — segment should be ignored.
        let pkt = segmentPacket(streamId: UUID(), isKeyframe: false, audio: Data([0x01]))
        try await svc.handlePacket(pkt)

        XCTAssertFalse(frameReceived, "onSegmentReceived must not fire when not subscribed")
    }
}
