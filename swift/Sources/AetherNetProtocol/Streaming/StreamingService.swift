// SPDX-License-Identifier: MIT
// NOTE: CI on Linux is the verification gate.

import Foundation

// ─── StreamingService ─────────────────────────────────────

/// Live-stream publish/subscribe service.
///
/// Wire format for StreamSegment payload:
///   [16] StreamId (UUID, RFC4122 big-endian)
///   [4]  Sequence (UInt32 little-endian)
///   [8]  TimestampMs (Int64 little-endian)
///   [1]  IsKeyframe (UInt8: 0 or 1)
///   [N]  EncodedPayload
///
/// Signalling uses JSON (Codable, snake_case) in MeshPacket.payload.
public actor StreamingService {
    private let sender: any MeshSender

    private var streams: [UUID: StreamRecord] = [:]
    private var subscriptions: Set<UUID> = []
    private var segmentSequence: [UUID: UInt32] = [:]

    public var onStreamAnnounced: (@Sendable (UUID, String, String) -> Void)?
    /// (streamId, data, isKeyframe, timestampMs, sequence)
    public var onSegmentReceived: (@Sendable (UUID, Data, Bool, Int64, UInt32) -> Void)?
    public var onStreamEnded: (@Sendable (UUID) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    // MARK: – Callbacks

    public func setOnStreamAnnounced(_ cb: (@Sendable (UUID, String, String) -> Void)?) {
        onStreamAnnounced = cb
    }
    public func setOnSegmentReceived(_ cb: (@Sendable (UUID, Data, Bool, Int64, UInt32) -> Void)?) {
        onSegmentReceived = cb
    }
    public func setOnStreamEnded(_ cb: (@Sendable (UUID) -> Void)?) {
        onStreamEnded = cb
    }

    // MARK: – Publisher side

    /// Announce a new stream to the mesh (broadcast to all peers).
    public func startStream(title: String, mimeType: String) async throws -> UUID {
        let streamId = UUID()
        streams[streamId] = StreamRecord(streamId: streamId, publisherUhid: sender.localUhid, title: title, mimeType: mimeType, subscribers: [])
        segmentSequence[streamId] = 0

        let wire = StreamAnnounceWire(stream_id: streamId, publisher_uhid: sender.localUhid, title: title, mime_type: mimeType)
        var pkt = MeshPacket(type: .streamAnnounce, sourceUhid: sender.localUhid, destinationUhid: "", ttl: ProtocolConstants.defaultTtl, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.broadcast(pkt)
        return streamId
    }

    /// End a stream — broadcasts termination notice.
    public func endStream(streamId: UUID) async throws {
        guard let record = streams[streamId] else { return }

        let wire = StreamEndWire(stream_id: streamId, publisher_uhid: sender.localUhid)
        let payload = encodeJSON(wire)
        // Notify known subscribers directly
        for sub in record.subscribers {
            var pkt = MeshPacket(type: .streamAnnounce, sourceUhid: sender.localUhid, destinationUhid: sub, priority: 32)
            pkt.payload = payload
            _ = await sender.send(pkt, nextHopUhid: sub)
        }
        // Also broadcast so non-subscriber nodes can clean up their caches
        var bcast = MeshPacket(type: .streamAnnounce, sourceUhid: sender.localUhid, destinationUhid: "", priority: 32)
        bcast.payload = payload
        _ = await sender.broadcast(bcast)

        streams.removeValue(forKey: streamId)
        segmentSequence.removeValue(forKey: streamId)
        onStreamEnded?(streamId)
    }

    /// Publish an encoded media segment to all current subscribers.
    public func publishSegment(streamId: UUID, encodedData: Data, isKeyframe: Bool) async throws {
        guard let record = streams[streamId] else { return }
        let seq = segmentSequence[streamId, default: 0]
        segmentSequence[streamId] = seq &+ 1
        let tsMs = Int64(Date().timeIntervalSince1970 * 1000)
        let frameData = encodeStreamSegment(streamId: streamId, sequence: seq, timestampMs: tsMs, isKeyframe: isKeyframe, payload: encodedData)

        for sub in record.subscribers {
            var pkt = MeshPacket(type: .streamSegment, sourceUhid: sender.localUhid, destinationUhid: sub, priority: 16)
            pkt.payload = frameData
            _ = await sender.send(pkt, nextHopUhid: sub)
        }
    }

    // MARK: – Subscriber side

    /// Subscribe to a stream published by `publisherUhid`.
    public func subscribe(streamId: UUID, publisherUhid: String) async throws {
        subscriptions.insert(streamId)
        let wire = StreamSubscribeWire(stream_id: streamId, subscriber_uhid: sender.localUhid)
        var pkt = MeshPacket(type: .streamSubscribe, sourceUhid: sender.localUhid, destinationUhid: publisherUhid, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.send(pkt, nextHopUhid: publisherUhid)
    }

    /// Unsubscribe from a stream.
    public func unsubscribe(streamId: UUID, publisherUhid: String) async throws {
        subscriptions.remove(streamId)
        let wire = StreamSubscribeWire(stream_id: streamId, subscriber_uhid: sender.localUhid)
        var pkt = MeshPacket(type: .streamUnsubscribe, sourceUhid: sender.localUhid, destinationUhid: publisherUhid, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.send(pkt, nextHopUhid: publisherUhid)
    }

    // MARK: – Inbound dispatch

    public func handlePacket(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .streamAnnounce:     handleStreamAnnounce(packet)
        case .streamSubscribe:    handleSubscribe(packet)
        case .streamUnsubscribe:  handleUnsubscribe(packet)
        case .streamSegment:      handleSegment(packet)
        default: break
        }
    }

    // MARK: – Private

    private func handleStreamAnnounce(_ packet: MeshPacket) {
        // Disambiguate by required fields: an announce carries title + mime_type,
        // an end notice does not (it carries signal_type:"end"). Decode the announce
        // first — it fails fast on an end notice (missing title/mime_type), whereas
        // StreamEndWire's defaulted signal_type would otherwise swallow an announce
        // that has no signal_type field at all.
        if let ann = decodeJSON(StreamAnnounceWire.self, from: packet.payload) {
            if streams[ann.stream_id] == nil {
                streams[ann.stream_id] = StreamRecord(streamId: ann.stream_id, publisherUhid: ann.publisher_uhid, title: ann.title, mimeType: ann.mime_type, subscribers: [])
            }
            onStreamAnnounced?(ann.stream_id, ann.publisher_uhid, ann.title)
            return
        }
        if let end = decodeJSON(StreamEndWire.self, from: packet.payload) {
            streams.removeValue(forKey: end.stream_id)
            subscriptions.remove(end.stream_id)
            onStreamEnded?(end.stream_id)
        }
    }

    private func handleSubscribe(_ packet: MeshPacket) {
        guard let wire = decodeJSON(StreamSubscribeWire.self, from: packet.payload) else { return }
        if var r = streams[wire.stream_id] {
            if !r.subscribers.contains(wire.subscriber_uhid) { r.subscribers.append(wire.subscriber_uhid) }
            streams[wire.stream_id] = r
        }
    }

    private func handleUnsubscribe(_ packet: MeshPacket) {
        guard let wire = decodeJSON(StreamSubscribeWire.self, from: packet.payload) else { return }
        if var r = streams[wire.stream_id] {
            r.subscribers.removeAll { $0 == wire.subscriber_uhid }
            streams[wire.stream_id] = r
        }
    }

    private func handleSegment(_ packet: MeshPacket) {
        guard let (streamId, seq, tsMs, isKeyframe, data) = decodeStreamSegment(packet.payload) else { return }
        if subscriptions.contains(streamId) {
            onSegmentReceived?(streamId, data, isKeyframe, tsMs, seq)
        }
    }
}

// ─── Internal model ───────────────────────────────────────

private struct StreamRecord: Sendable {
    var streamId: UUID
    var publisherUhid: String
    var title: String
    var mimeType: String
    var subscribers: [String]
}

// ─── JSON wire types ──────────────────────────────────────

private struct StreamAnnounceWire: Codable {
    @LowercaseUUIDCoding var stream_id: UUID
    let publisher_uhid: String
    let title: String
    let mime_type: String
    // No signal_type — absence disambiguates from end notice during decode
}

private struct StreamEndWire: Codable {
    @LowercaseUUIDCoding var stream_id: UUID
    let publisher_uhid: String
    let signal_type: String = "end"
    private enum CodingKeys: String, CodingKey {
        case stream_id, publisher_uhid, signal_type
    }
}

private struct StreamSubscribeWire: Codable {
    @LowercaseUUIDCoding var stream_id: UUID
    let subscriber_uhid: String
}

// ─── Binary StreamSegment helpers ────────────────────────
// Layout: [16 StreamId BE][4 Seq LE][8 TsMs LE][1 IsKeyframe][N Data]

private func encodeStreamSegment(streamId: UUID, sequence: UInt32, timestampMs: Int64, isKeyframe: Bool, payload: Data) -> Data {
    var buf = Data(capacity: 29 + payload.count)
    var uuidBytes = streamId.uuid
    withUnsafeBytes(of: &uuidBytes) { buf.append(contentsOf: $0) }
    var seq = sequence.littleEndian
    withUnsafeBytes(of: &seq) { buf.append(contentsOf: $0) }
    var ts = timestampMs.littleEndian
    withUnsafeBytes(of: &ts) { buf.append(contentsOf: $0) }
    buf.append(isKeyframe ? 1 : 0)
    buf.append(payload)
    return buf
}

private func decodeStreamSegment(_ data: Data) -> (UUID, UInt32, Int64, Bool, Data)? {
    guard data.count >= 29 else { return nil }
    let streamId   = UUID(uuid: data.subdata(in: 0..<16).withUnsafeBytes { $0.load(as: uuid_t.self) })
    let seq        = UInt32(littleEndian: data.subdata(in: 16..<20).withUnsafeBytes { $0.load(as: UInt32.self) })
    let tsMs       = Int64(littleEndian: data.subdata(in: 20..<28).withUnsafeBytes { $0.load(as: Int64.self) })
    let isKeyframe = data[28] != 0
    return (streamId, seq, tsMs, isKeyframe, data.subdata(in: 29..<data.count))
}

private func encodeJSON<T: Encodable>(_ value: T) -> Data {
    (try? JSONEncoder().encode(value)) ?? Data()
}

private func decodeJSON<T: Decodable>(_ type: T.Type, from data: Data) -> T? {
    try? JSONDecoder().decode(type, from: data)
}
