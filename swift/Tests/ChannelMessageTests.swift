// SPDX-License-Identifier: MIT
import XCTest
import Foundation
@testable import AetherNetProtocol

/// Unit tests for ``ChannelMessageService`` (PacketType.channelMessage). Mirrors the C#
/// `ChannelMessageTests`. A ``FakeMeshSender`` captures broadcasts — no transport needed.
///
/// The byte-identity vectors match `fixtures/channels/vectors.json` and the C# `[InlineData]`
/// vectors exactly (snake_case keys, field order channel_id, message_id, sender_uhid, content,
/// sent_at_ms, no whitespace, lowercase-dashed UUID, sent_at_ms a bare integer).
final class ChannelMessageTests: XCTestCase {

    private static let LOCAL = "aether:local:01"

    /// Mirror used only to DECODE captured ChannelMessage payloads in assertions. The real wire
    /// struct (`ChannelMessageWire`) is `private` to the service; byte-identity of the real encoder
    /// is verified separately via `_channelMessageWireBytesForTests`.
    private struct ChannelMessageWireMirror: Codable {
        let channel_id: String
        let message_id: String
        let sender_uhid: String
        let content: String
        let sent_at_ms: Int64
    }

    /// Build an inbound ChannelMessage packet, serialised via the real wire encoder.
    private func channelPacket(
        channelId: String,
        messageId: UUID,
        sender: String,
        content: String,
        sentAtMs: Int64,
        ttl: Int32 = 7
    ) -> MeshPacket {
        MeshPacket(
            type: .channelMessage,
            sourceUhid: sender,
            destinationUhid: "*",
            ttl: ttl,
            payload: _channelMessageWireBytesForTests(
                channelId: channelId,
                messageId: messageId,
                senderUhid: sender,
                content: content,
                sentAtMs: sentAtMs
            )
        )
    }

    // MARK: - Byte-identity vectors (fixtures/channels/vectors.json)

    func test_channelMessageWire_basicVector_serializesExactBytes() {
        let id = UUID(uuidString: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")!
        let data = _channelMessageWireBytesForTests(
            channelId: "res-floor-3",
            messageId: id,
            senderUhid: "aether:alice:01",
            content: "meeting at 6",
            sentAtMs: 1_700_000_000_000
        )
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"channel_id\":\"res-floor-3\",\"message_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"sender_uhid\":\"aether:alice:01\",\"content\":\"meeting at 6\",\"sent_at_ms\":1700000000000}"
        )
    }

    func test_channelMessageWire_minimalVector_serializesExactBytes() {
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!
        let data = _channelMessageWireBytesForTests(
            channelId: "g",
            messageId: id,
            senderUhid: "n",
            content: "",
            sentAtMs: 0
        )
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"channel_id\":\"g\",\"message_id\":\"00000000-0000-0000-0000-000000000000\",\"sender_uhid\":\"n\",\"content\":\"\",\"sent_at_ms\":0}"
        )
    }

    // MARK: - Publish

    func test_publish_broadcastsChannelMessage() async {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = ChannelMessageService(sender: sender)

        _ = await svc.publish("res-floor-3", content: "meeting at 6")

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        let pkt = broadcasts[0]
        XCTAssertEqual(pkt.type, .channelMessage)
        XCTAssertEqual(pkt.destinationUhid, "*")
        XCTAssertEqual(pkt.ttl, ProtocolConstants.defaultTtl)

        let body = try? JSONDecoder().decode(ChannelMessageWireMirror.self, from: pkt.payload)
        XCTAssertEqual(body?.channel_id, "res-floor-3")
        XCTAssertEqual(body?.content, "meeting at 6")
        XCTAssertEqual(body?.sender_uhid, "aether:alice:01")
    }

    // MARK: - Handle

    func test_handle_subscribedChannel_raisesEvent() async {
        let svc = ChannelMessageService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        await svc.subscribe("res-floor-3")

        let got = Locked<ChannelMessageReceived?>(nil)
        await svc.setOnMessageReceived { e in got.value = e }

        let ok = await svc.handle(channelPacket(
            channelId: "res-floor-3",
            messageId: UUID(),
            sender: "aether:bob:02",
            content: "hello floor",
            sentAtMs: 1_700_000_000_000
        ))

        XCTAssertTrue(ok)
        XCTAssertNotNil(got.value)
        XCTAssertEqual(got.value?.channelId, "res-floor-3")
        XCTAssertEqual(got.value?.content, "hello floor")
        XCTAssertEqual(got.value?.senderUhid, "aether:bob:02")
    }

    func test_handle_unsubscribedChannel_noEventButProcessed() async {
        let svc = ChannelMessageService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        let raised = Locked<Bool>(false)
        await svc.setOnMessageReceived { _ in raised.value = true }

        let ok = await svc.handle(channelPacket(
            channelId: "society-x",
            messageId: UUID(),
            sender: "aether:bob:02",
            content: "hi",
            sentAtMs: 1
        ))

        XCTAssertTrue(ok)              // processed + relayed
        XCTAssertFalse(raised.value)   // but not surfaced — we aren't subscribed
    }

    func test_handle_duplicateMessageId_returnsFalse() async {
        let svc = ChannelMessageService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        await svc.subscribe("res-floor-3")
        let id = UUID()

        let events = Locked<Int>(0)
        await svc.setOnMessageReceived { _ in events.value = events.value + 1 }

        let first = await svc.handle(channelPacket(
            channelId: "res-floor-3", messageId: id, sender: "aether:bob:02", content: "one", sentAtMs: 1))
        let second = await svc.handle(channelPacket(
            channelId: "res-floor-3", messageId: id, sender: "aether:bob:02", content: "one", sentAtMs: 1))

        XCTAssertTrue(first)
        XCTAssertFalse(second)
        XCTAssertEqual(events.value, 1)
    }

    func test_handle_wrongPacketType_returnsFalse() async {
        let svc = ChannelMessageService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        var pkt = channelPacket(
            channelId: "res-floor-3", messageId: UUID(), sender: "aether:bob:02", content: "x", sentAtMs: 1)
        pkt.type = .data
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    func test_handle_relaysWhenTtlAllows() async {
        let relaySender = FakeMeshSender(localUhid: "aether:relay:09")
        let svc = ChannelMessageService(sender: relaySender) // not subscribed — pure relay
        _ = await svc.handle(channelPacket(
            channelId: "res-floor-3", messageId: UUID(), sender: "aether:bob:02", content: "hop", sentAtMs: 1, ttl: 5))

        let broadcasts = relaySender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        XCTAssertEqual(broadcasts[0].type, .channelMessage)
        XCTAssertEqual(broadcasts[0].ttl, 4)
    }

    func test_handle_ownMessage_notSurfacedAndNotRelayed() async {
        // A node's own message flooding back: subscribed to the channel, but sender == local.
        let sender = FakeMeshSender(localUhid: "aether:me:01")
        let svc = ChannelMessageService(sender: sender)
        await svc.subscribe("res-floor-3")
        let raised = Locked<Bool>(false)
        await svc.setOnMessageReceived { _ in raised.value = true }

        let ok = await svc.handle(channelPacket(
            channelId: "res-floor-3", messageId: UUID(), sender: "aether:me:01", content: "mine", sentAtMs: 1, ttl: 5))

        XCTAssertTrue(ok)                       // de-dup slot consumed → processed
        XCTAssertFalse(raised.value)            // not surfaced — it is our own
        XCTAssertEqual(sender.broadcasts().count, 0) // not relayed — it is our own
    }

    // MARK: - Subscriptions

    func test_subscriptions_trackAndUntrack() async {
        let svc = ChannelMessageService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        await svc.subscribe("a")
        await svc.subscribe("b")
        var subs = await svc.getSubscriptions()
        XCTAssertEqual(Set(subs), ["a", "b"])

        await svc.unsubscribe("a")
        subs = await svc.getSubscriptions()
        XCTAssertEqual(Set(subs), ["b"])
    }
}
