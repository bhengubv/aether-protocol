// SPDX-License-Identifier: MIT

import Foundation

/// Event surfaced when a channel message arrives on a channel this node is subscribed to.
/// Mirrors C# `ChannelMessageReceived`.
public struct ChannelMessageReceived: Sendable, Equatable {
    /// Channel the message was published to.
    public let channelId: String
    /// Unique id of the message.
    public let messageId: UUID
    /// UHID of the original author (preserved across relay hops).
    public let senderUhid: String
    /// Message body.
    public let content: String
    /// Unix-ms timestamp the author published the message.
    public let sentAtMs: Int64

    public init(channelId: String, messageId: UUID, senderUhid: String, content: String, sentAtMs: Int64) {
        self.channelId = channelId
        self.messageId = messageId
        self.senderUhid = senderUhid
        self.content = content
        self.sentAtMs = sentAtMs
    }
}

/// Application-layer named-channel pub/sub over ``PacketType/channelMessage`` (PacketType 7).
///
/// A node subscribes to the channel ids it cares about; publishing floods the mesh; subscribed
/// receivers surface the message via ``onMessageReceived``. Messages are de-duplicated by their
/// message id and re-flooded (TTL-bounded) so they reach subscribers several hops away.
///
/// Mirrors C# `ChannelMessageService`. The original author is carried in the payload's
/// `sender_uhid` so it survives relay hops (the enclosing packet's `sourceUhid` changes each hop).
public actor ChannelMessageService {
    private let sender: any MeshSender

    private var subscriptions: Set<String> = []
    private var seen: Set<UUID> = []

    /// Raised when a message arrives on a subscribed channel (never raised for this node's own messages).
    public var onMessageReceived: (@Sendable (ChannelMessageReceived) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnMessageReceived(_ callback: (@Sendable (ChannelMessageReceived) -> Void)?) {
        onMessageReceived = callback
    }

    /// Subscribe to a channel — messages on it will fire ``onMessageReceived``.
    public func subscribe(_ channelId: String) {
        guard !channelId.isEmpty else { return }
        subscriptions.insert(channelId)
    }

    /// Stop surfacing messages for a channel.
    public func unsubscribe(_ channelId: String) {
        subscriptions.remove(channelId)
    }

    /// The channels this node is currently subscribed to.
    public func getSubscriptions() -> [String] {
        Array(subscriptions)
    }

    /// Publish `content` to `channelId`: floods a ``PacketType/channelMessage`` to all peers.
    /// Returns the number of peers reached directly. No-op (returns 0) for an empty channel id.
    @discardableResult
    public func publish(_ channelId: String, content: String) async -> Int {
        guard !channelId.isEmpty else { return 0 }

        let messageId = UUID()
        let body = encodeChannelMessageWire(
            channelId: channelId,
            messageId: messageId,
            senderUhid: sender.localUhid,
            content: content,
            sentAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )
        seen.insert(messageId) // never re-handle our own message when it floods back

        let packet = MeshPacket(
            type: .channelMessage,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )

        return await sender.broadcast(packet)
    }

    /// Process an incoming ``PacketType/channelMessage`` packet: de-dup by message id, surface it if
    /// we are subscribed to its channel (and it is not our own), and re-flood while TTL allows.
    /// Returns false for the wrong packet type, a malformed payload, or a duplicate.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .channelMessage else { return false }

        guard let body = parseChannelMessageWire(packet.payload), !body.channelId.isEmpty else {
            return false
        }

        // Flood de-duplication: only the first copy of a given message id is processed.
        guard seen.insert(body.messageId).inserted else { return false }

        let isOwn = body.senderUhid == sender.localUhid
        if !isOwn && subscriptions.contains(body.channelId) {
            onMessageReceived?(ChannelMessageReceived(
                channelId: body.channelId,
                messageId: body.messageId,
                senderUhid: body.senderUhid,
                content: body.content,
                sentAtMs: body.sentAtMs
            ))
        }

        // Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
        if packet.ttl > 1 && !isOwn {
            var fwd = packet
            fwd.ttl = packet.ttl - 1
            _ = await sender.broadcast(fwd)
        }

        return true
    }
}

// ─── ChannelMessage wire (PacketType 7) ───
//
// Serialises to snake_case keys, field order channel_id, message_id, sender_uhid, content,
// sent_at_ms, no whitespace, GUID lowercase-dashed, sent_at_ms a bare integer. This is the
// byte-identity gate (fixtures/channels/vectors.json).

private struct ChannelMessageWire: Codable {
    let channel_id: String
    @LowercaseUUIDCoding var message_id: UUID
    let sender_uhid: String
    let content: String
    let sent_at_ms: Int64
    // Lock the wire field order explicitly (channel_id, message_id, sender_uhid, content,
    // sent_at_ms) — matches the convention used by the other multi-field wrapped wire structs
    // (WatchInviteWire, StreamEndWire) so the byte-identity gate never depends on synthesis order.
    private enum CodingKeys: String, CodingKey {
        case channel_id, message_id, sender_uhid, content, sent_at_ms
    }
}

private func encodeChannelMessageWire(
    channelId: String,
    messageId: UUID,
    senderUhid: String,
    content: String,
    sentAtMs: Int64
) -> Data {
    let w = ChannelMessageWire(
        channel_id: channelId,
        message_id: messageId,
        sender_uhid: senderUhid,
        content: content,
        sent_at_ms: sentAtMs
    )
    return (try? JSONEncoder().encode(w)) ?? Data()
}

private func parseChannelMessageWire(
    _ data: Data
) -> (channelId: String, messageId: UUID, senderUhid: String, content: String, sentAtMs: Int64)? {
    guard let w = try? JSONDecoder().decode(ChannelMessageWire.self, from: data) else { return nil }
    return (w.channel_id, w.message_id, w.sender_uhid, w.content, w.sent_at_ms)
}

/// Test-only shim exposing the real ``ChannelMessageWire`` serialization path (the struct itself
/// stays `private`) so byte-identity vectors in `fixtures/channels/vectors.json` can be verified.
internal func _channelMessageWireBytesForTests(
    channelId: String,
    messageId: UUID,
    senderUhid: String,
    content: String,
    sentAtMs: Int64
) -> Data {
    encodeChannelMessageWire(
        channelId: channelId,
        messageId: messageId,
        senderUhid: senderUhid,
        content: content,
        sentAtMs: sentAtMs
    )
}
