// SPDX-License-Identifier: MIT

import Foundation

// ─── BandwidthProbe ───────────────────────────────────────

/// A latency/throughput probe request (``PacketType/bandwidthProbe`` = 53 body).
///
/// Mirrors C# `BandwidthProbe`. ``BandwidthProbeAck``/``BandwidthGossipPayload`` already
/// exist (see ``BandwidthModels``); this is the one wire type the codec still needed.
public struct BandwidthProbe: Sendable, Equatable {
    public let sequence: UInt32
    public let senderSendUs: Int64

    public init(sequence: UInt32, senderSendUs: Int64) {
        self.sequence = sequence
        self.senderSendUs = senderSendUs
    }
}

// ─── BandwidthProbeReceived ───────────────────────────────

/// Event payload: an inbound probe plus the peer that sent it (so the host can reply
/// with an ack). Mirrors C# `BandwidthProbeReceived`.
public struct BandwidthProbeReceived: Sendable, Equatable {
    public let probe: BandwidthProbe
    public let fromUhid: String

    public init(probe: BandwidthProbe, fromUhid: String) {
        self.probe = probe
        self.fromUhid = fromUhid
    }
}

// ─── BandwidthWireCodec ───────────────────────────────────
//
// Binary wire codec for the three ABMF packets. All multi-byte integers are LITTLE-ENDIAN,
// matching the packet-serializer convention. NO version byte — the layouts are the ones
// documented on the PacketType members. Byte-identity gate: fixtures/bandwidth/vectors.json.
//
//   Probe(53)  : sequence u32 | sender_send_us i64                                              (12 B)
//   Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
//   Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                   (13 B)
//
// senderReceiveUs is NOT on the wire — the prober fills it locally on receipt (0 on decode).
// peerUhid/transportName/measuredAt of a gossip come from the enclosing packet + local clock,
// not the wire body. Bytes are appended explicitly (manual shifts), never via host endianness —
// the same idiom the DTN envelope serializer uses.

/// Mirrors C# `BandwidthWireCodec`.
public enum BandwidthWireCodec {

    // MARK: - Probe (53)

    public static func serializeProbe(_ p: BandwidthProbe) -> Data {
        var out = Data()
        appendU32(&out, p.sequence)
        appendI64(&out, p.senderSendUs)
        return out
    }

    /// Decode a probe body. Returns nil if shorter than 12 bytes.
    public static func deserializeProbe(_ data: Data) -> BandwidthProbe? {
        var r = Reader(data)
        guard let sequence = r.u32(),
              let senderSendUs = r.i64()
        else { return nil }
        return BandwidthProbe(sequence: sequence, senderSendUs: senderSendUs)
    }

    // MARK: - Ack (54)

    public static func serializeAck(_ a: BandwidthProbeAck) -> Data {
        var out = Data()
        appendU32(&out, a.sequence)
        appendI64(&out, a.senderSendUs)
        appendI64(&out, a.receiverReceiveUs)
        appendI64(&out, a.receiverSendUs)
        appendI32(&out, a.probeBytes)
        return out
    }

    /// Decode an ack body. `senderReceiveUs` is filled by the prober on receipt, not carried
    /// on the wire, so it decodes to 0. Returns nil if shorter than 32 bytes.
    public static func deserializeAck(_ data: Data) -> BandwidthProbeAck? {
        var r = Reader(data)
        guard let sequence = r.u32(),
              let senderSendUs = r.i64(),
              let receiverReceiveUs = r.i64(),
              let receiverSendUs = r.i64(),
              let probeBytes = r.i32()
        else { return nil }
        return BandwidthProbeAck(
            sequence: sequence,
            senderSendUs: senderSendUs,
            receiverReceiveUs: receiverReceiveUs,
            receiverSendUs: receiverSendUs,
            senderReceiveUs: 0, // not on wire — filled by the prober on receipt
            probeBytes: probeBytes
        )
    }

    // MARK: - Gossip (55)

    public static func serializeGossip(_ g: BandwidthGossipPayload) -> Data {
        var out = Data()
        appendI64(&out, g.btlBwBps)
        // rtPropUs is Int64 on the model but an i32 field on the wire — clamp to [0, Int32.max]
        // exactly as the C# codec does (Math.Clamp).
        let clamped = Int32(min(max(g.rtPropUs, 0), Int64(Int32.max)))
        appendI32(&out, clamped)
        out.append(g.confidence.rawValue)
        return out
    }

    /// Decode a gossip body. peerUhid/transportName default to empty and measuredAt to a zero
    /// date; the service fills peerUhid from the packet source. Returns nil if shorter than 13 bytes.
    public static func deserializeGossip(_ data: Data) -> BandwidthGossipPayload? {
        var r = Reader(data)
        guard let btlBwBps = r.i64(),
              let rtPropI32 = r.i32(),
              let confByte = r.u8(),
              let confidence = BandwidthConfidence(rawValue: confByte)
        else { return nil }
        return BandwidthGossipPayload(
            peerUhid: "",
            transportName: "",
            btlBwBps: btlBwBps,
            rtPropUs: Int64(rtPropI32),
            confidence: confidence,
            measuredAt: Date(timeIntervalSince1970: 0)
        )
    }

    // MARK: - primitives (little-endian, manual shifts — never host endianness)

    private static func appendU32(_ out: inout Data, _ v: UInt32) {
        out.append(UInt8(v & 0xff))
        out.append(UInt8((v >> 8) & 0xff))
        out.append(UInt8((v >> 16) & 0xff))
        out.append(UInt8((v >> 24) & 0xff))
    }

    private static func appendI32(_ out: inout Data, _ v: Int32) {
        appendU32(&out, UInt32(bitPattern: v))
    }

    private static func appendI64(_ out: inout Data, _ v: Int64) {
        let u = UInt64(bitPattern: v)
        for i in 0..<8 { out.append(UInt8((u >> (8 * i)) & 0xff)) }
    }

    private struct Reader {
        let d: [UInt8]
        var o = 0

        init(_ data: Data) { d = Array(data) }

        mutating func u8() -> UInt8? {
            guard o + 1 <= d.count else { return nil }
            defer { o += 1 }
            return d[o]
        }

        mutating func u32() -> UInt32? {
            guard o + 4 <= d.count else { return nil }
            let u = UInt32(d[o]) | (UInt32(d[o + 1]) << 8) | (UInt32(d[o + 2]) << 16) | (UInt32(d[o + 3]) << 24)
            o += 4
            return u
        }

        mutating func i32() -> Int32? {
            guard let u = u32() else { return nil }
            return Int32(bitPattern: u)
        }

        mutating func i64() -> Int64? {
            guard o + 8 <= d.count else { return nil }
            var u: UInt64 = 0
            for i in 0..<8 { u |= UInt64(d[o + i]) << (8 * i) }
            o += 8
            return Int64(bitPattern: u)
        }
    }
}

// ─── BandwidthWireService ─────────────────────────────────

/// Binds the three ABMF ``PacketType``s to the mesh: send probes (directed) + their acks
/// (directed reply), and broadcast/receive warm-start gossip. Inbound packets surface via
/// callbacks; the host feeds them into the estimator (recordProbeResult / warmFromGossip) and
/// replies to probes.
///
/// A `public actor`, mirroring ``VideoCallControlService``. The C# reference surfaces inbound
/// packets via C# `event`s; Swift uses `@Sendable` callback properties set through the actor.
/// Mirrors C# `BandwidthWireService`.
public actor BandwidthWireService {
    private let sender: any MeshSender

    /// Raised when an inbound probe arrives (with the peer that sent it, so the host can ack).
    public var onProbeReceived: (@Sendable (BandwidthProbeReceived) -> Void)?
    /// Raised when an inbound probe ack arrives.
    public var onAckReceived: (@Sendable (BandwidthProbeAck) -> Void)?
    /// Raised when inbound warm-start gossip arrives (peerUhid set from the packet source).
    public var onGossipReceived: (@Sendable (BandwidthGossipPayload) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnProbeReceived(_ callback: (@Sendable (BandwidthProbeReceived) -> Void)?) {
        onProbeReceived = callback
    }
    public func setOnAckReceived(_ callback: (@Sendable (BandwidthProbeAck) -> Void)?) {
        onAckReceived = callback
    }
    public func setOnGossipReceived(_ callback: (@Sendable (BandwidthGossipPayload) -> Void)?) {
        onGossipReceived = callback
    }

    // MARK: – Outbound

    /// Send a directed ``PacketType/bandwidthProbe`` to a peer. Returns delivery success.
    @discardableResult
    public func sendProbe(_ peerUhid: String, probe: BandwidthProbe) async -> Bool {
        guard !peerUhid.isEmpty else { return false }
        return await sendDirected(
            peerUhid: peerUhid,
            type: .bandwidthProbe,
            payload: BandwidthWireCodec.serializeProbe(probe)
        )
    }

    /// Send a directed ``PacketType/bandwidthAck`` reply to the prober. Returns delivery success.
    @discardableResult
    public func sendAck(_ peerUhid: String, ack: BandwidthProbeAck) async -> Bool {
        guard !peerUhid.isEmpty else { return false }
        return await sendDirected(
            peerUhid: peerUhid,
            type: .bandwidthAck,
            payload: BandwidthWireCodec.serializeAck(ack)
        )
    }

    private func sendDirected(peerUhid: String, type: PacketType, payload: Data) async -> Bool {
        let packet = MeshPacket(
            type: type,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: payload
        )
        return await sender.send(packet, nextHopUhid: peerUhid)
    }

    /// Broadcast a ``PacketType/bandwidthGossip`` warm-start estimate. Returns peers reached.
    @discardableResult
    public func broadcastGossip(_ gossip: BandwidthGossipPayload) async -> Int {
        let packet = MeshPacket(
            type: .bandwidthGossip,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: ProtocolConstants.defaultTtl,
            payload: BandwidthWireCodec.serializeGossip(gossip)
        )
        return await sender.broadcast(packet)
    }

    // MARK: – Inbound dispatch

    /// Dispatch an inbound bandwidth packet to the matching callback. Returns false on the wrong
    /// packet type or a malformed body, true once the event has been surfaced.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        switch packet.type {
        case .bandwidthProbe:
            guard let probe = BandwidthWireCodec.deserializeProbe(packet.payload) else { return false }
            onProbeReceived?(BandwidthProbeReceived(probe: probe, fromUhid: packet.sourceUhid))
            return true

        case .bandwidthAck:
            guard let ack = BandwidthWireCodec.deserializeAck(packet.payload) else { return false }
            onAckReceived?(ack)
            return true

        case .bandwidthGossip:
            guard let body = BandwidthWireCodec.deserializeGossip(packet.payload) else { return false }
            // peerUhid is not on the wire — the service fills it from the packet source.
            let gossip = BandwidthGossipPayload(
                peerUhid: packet.sourceUhid,
                transportName: body.transportName,
                btlBwBps: body.btlBwBps,
                rtPropUs: body.rtPropUs,
                confidence: body.confidence,
                measuredAt: body.measuredAt
            )
            onGossipReceived?(gossip)
            return true

        default:
            return false
        }
    }
}
