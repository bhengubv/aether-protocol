// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Unit tests for the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54),
/// BandwidthGossip(55). Binary little-endian byte-identity gates + send/handle behaviour.
///
/// Mirrors the C# `BandwidthWireTests` case-for-case:
///   1. Byte-identity — each codec must serialize to EXACTLY the LITTLE-ENDIAN bytes pinned in
///      `fixtures/bandwidth/vectors.json` (bytes → lowercase hex). This is the cross-language gate.
///      Belt-and-braces inline cases pin the same bytes even if the fixture file moves.
///   2. Behaviour — directed probe/ack sends captured by the shared ``FakeMeshSender``; gossip
///      broadcast; handle() raises the matching callback (gossip stamps peerUhid from the source);
///      a wrong packet type is rejected.
///
/// ``BandwidthWireService`` is a `public actor`, so every drive method is `async`. Actor-isolated
/// getters (the callback boxes below are read on the main test task) are read into a `let` before
/// any `XCTUnwrap`/`XCTAssertEqual` — never `await` inside an autoclosure.
final class BandwidthWireTests: XCTestCase {

    // MARK: - Fakes / capture

    // Directed/broadcast sends are captured by the shared `FakeMeshSender`
    // (Tests/FakeMeshSender.swift), a `final class` whose `unicasts()` / `broadcasts()` return the
    // recorded packets. A per-test nested `actor` fake is deliberately avoided: an `actor`
    // conforming to the nonisolated `MeshSender` requirements trips Swift 6 `#ConformanceIsolation`.

    /// Thread-safe one-shot capture box for a `@Sendable` callback value.
    private final class Box<T: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var value: T?
        func set(_ v: T) { lock.lock(); value = v; lock.unlock() }
        func get() -> T? { lock.lock(); defer { lock.unlock() }; return value }
    }

    // MARK: - Byte-identity corpus

    private struct Vector: Decodable {
        let name: String
        let kind: String
        let sequence: UInt32?
        let sender_send_us: Int64?
        let receiver_receive_us: Int64?
        let receiver_send_us: Int64?
        let probe_bytes: Int32?
        let btlbw_bps: Int64?
        let rtprop_us: Int32?
        let confidence: UInt8?
        let expected_hex: String
    }

    private struct Corpus: Decodable {
        let description: String
        let vectors: [Vector]
    }

    /// Locate `fixtures/bandwidth/vectors.json` by walking up from this source file's directory
    /// (`#file`) to the repo root — independent of CWD, the same parent-traversal idiom the URI,
    /// Bandwidth, and VideoCallControl fixture drivers use.
    private func loadCorpus() throws -> Corpus {
        var url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = url
                .appendingPathComponent("fixtures")
                .appendingPathComponent("bandwidth")
                .appendingPathComponent("vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(Corpus.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/bandwidth/vectors.json walking up from \(#file)")
        throw CocoaError(.fileNoSuchFile)
    }

    private static func hex(_ d: Data) -> String {
        d.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Byte-identity gates (fixture-driven)

    /// Every fixture vector must serialize to EXACTLY its `expected_hex` bytes.
    func testWire_MatchesCanonicalVectors() throws {
        let corpus = try loadCorpus()
        XCTAssertFalse(corpus.vectors.isEmpty, "corpus has no vectors")

        for v in corpus.vectors {
            let bytes: Data
            switch v.kind {
            case "probe":
                bytes = BandwidthWireCodec.serializeProbe(BandwidthProbe(
                    sequence: try XCTUnwrap(v.sequence, "[\(v.name)] missing sequence"),
                    senderSendUs: try XCTUnwrap(v.sender_send_us, "[\(v.name)] missing sender_send_us")
                ))
            case "ack":
                bytes = BandwidthWireCodec.serializeAck(BandwidthProbeAck(
                    sequence: try XCTUnwrap(v.sequence, "[\(v.name)] missing sequence"),
                    senderSendUs: try XCTUnwrap(v.sender_send_us, "[\(v.name)] missing sender_send_us"),
                    receiverReceiveUs: try XCTUnwrap(v.receiver_receive_us, "[\(v.name)] missing receiver_receive_us"),
                    receiverSendUs: try XCTUnwrap(v.receiver_send_us, "[\(v.name)] missing receiver_send_us"),
                    senderReceiveUs: 999, // local-only — must NOT change the wire bytes
                    probeBytes: try XCTUnwrap(v.probe_bytes, "[\(v.name)] missing probe_bytes")
                ))
            case "gossip":
                let conf = try XCTUnwrap(BandwidthConfidence(rawValue: try XCTUnwrap(v.confidence, "[\(v.name)] missing confidence")),
                                         "[\(v.name)] bad confidence")
                bytes = BandwidthWireCodec.serializeGossip(BandwidthGossipPayload(
                    peerUhid: "peer",        // not on the wire
                    transportName: "tp",     // not on the wire
                    btlBwBps: try XCTUnwrap(v.btlbw_bps, "[\(v.name)] missing btlbw_bps"),
                    rtPropUs: Int64(try XCTUnwrap(v.rtprop_us, "[\(v.name)] missing rtprop_us")),
                    confidence: conf,
                    measuredAt: Date() // not on the wire
                ))
            default:
                XCTFail("[\(v.name)] unknown kind \(v.kind)")
                continue
            }
            XCTAssertEqual(Self.hex(bytes), v.expected_hex, "[\(v.name)] wire byte mismatch")
        }
    }

    // MARK: - Byte-identity gates (inline, mirroring the C# InlineData / literal cases)

    func testProbe_SerializesToCanonicalBytes() {
        XCTAssertEqual(
            Self.hex(BandwidthWireCodec.serializeProbe(BandwidthProbe(sequence: 42, senderSendUs: 1_700_000_000_000_000))),
            "2a00000000401e18240a0600"
        )
    }

    func testAck_SerializesToCanonicalBytes() {
        // senderReceiveUs (999) is local-only and must NOT change the wire bytes.
        let ack = BandwidthProbeAck(
            sequence: 42,
            senderSendUs: 1_700_000_000_000_000,
            receiverReceiveUs: 1_700_000_000_012_345,
            receiverSendUs: 1_700_000_000_013_000,
            senderReceiveUs: 999,
            probeBytes: 1200
        )
        XCTAssertEqual(
            Self.hex(BandwidthWireCodec.serializeAck(ack)),
            "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000"
        )
    }

    func testGossip_SerializesToCanonicalBytes() {
        // peerUhid/transportName/measuredAt are not on the wire.
        let g = BandwidthGossipPayload(
            peerUhid: "peer", transportName: "tp",
            btlBwBps: 5_000_000, rtPropUs: 25_000,
            confidence: .medium, measuredAt: Date(timeIntervalSince1970: 0)
        )
        XCTAssertEqual(Self.hex(BandwidthWireCodec.serializeGossip(g)), "404b4c0000000000a861000002")
    }

    func testAck_RoundTrips_SenderReceiveUsZeroed() throws {
        let back = try XCTUnwrap(BandwidthWireCodec.deserializeAck(
            BandwidthWireCodec.serializeAck(BandwidthProbeAck(
                sequence: 7, senderSendUs: 100, receiverReceiveUs: 200,
                receiverSendUs: 300, senderReceiveUs: 400, probeBytes: 512
            ))
        ))
        XCTAssertEqual(back.sequence, 7)
        XCTAssertEqual(back.senderSendUs, 100)
        XCTAssertEqual(back.receiverReceiveUs, 200)
        XCTAssertEqual(back.receiverSendUs, 300)
        XCTAssertEqual(back.senderReceiveUs, 0) // not on wire
        XCTAssertEqual(back.probeBytes, 512)
    }

    // MARK: - Behaviour

    /// sendProbe directed-sends a BandwidthProbe packet to the peer.
    func testSendProbe_EmitsDirectedProbe() async throws {
        let sender = FakeMeshSender(localUhid: "aether:a:01")
        let svc = BandwidthWireService(sender: sender)

        let ok = await svc.sendProbe("aether:b:02", probe: BandwidthProbe(sequence: 42, senderSendUs: 1_700_000_000_000_000))
        XCTAssertTrue(ok)

        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        let sent = try XCTUnwrap(sends.first)
        XCTAssertEqual(sent.packet.type, .bandwidthProbe)
        XCTAssertEqual(sent.nextHopUhid, "aether:b:02")
        XCTAssertEqual(sent.packet.destinationUhid, "aether:b:02")
        XCTAssertEqual(sent.packet.sourceUhid, "aether:a:01")
    }

    /// sendAck directed-sends a BandwidthAck packet to the prober.
    func testSendAck_EmitsDirectedAck() async throws {
        let sender = FakeMeshSender(localUhid: "aether:local:01")
        let svc = BandwidthWireService(sender: sender)

        let ack = BandwidthProbeAck(sequence: 1, senderSendUs: 2, receiverReceiveUs: 3,
                                    receiverSendUs: 4, senderReceiveUs: 5, probeBytes: 6)
        let ok = await svc.sendAck("aether:b:02", ack: ack)
        XCTAssertTrue(ok)

        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        XCTAssertEqual(try XCTUnwrap(sends.first).packet.type, .bandwidthAck)
    }

    /// broadcastGossip emits a gossip broadcast; handle() on it raises onGossipReceived with the
    /// peerUhid stamped from the packet source.
    func testBroadcastGossip_EmitsGossip_AndHandleRaisesEvent_WithSourcePeer() async throws {
        let sender = FakeMeshSender(localUhid: "aether:local:01")
        // Seed 3 peers so broadcast fan-out returns 3, matching the C# fake's fixed 3.
        sender.addPeer(PeerInfo(uhid: "aether:p:1"))
        sender.addPeer(PeerInfo(uhid: "aether:p:2"))
        sender.addPeer(PeerInfo(uhid: "aether:p:3"))
        let svc = BandwidthWireService(sender: sender)

        let g = BandwidthGossipPayload(peerUhid: "", transportName: "",
                                       btlBwBps: 5_000_000, rtPropUs: 25_000,
                                       confidence: .medium, measuredAt: Date(timeIntervalSince1970: 0))
        let reached = await svc.broadcastGossip(g)
        XCTAssertEqual(reached, 3)

        let casts = sender.broadcasts()
        XCTAssertEqual(casts.count, 1)
        var sent = try XCTUnwrap(casts.first)
        XCTAssertEqual(sent.type, .bandwidthGossip)

        let box = Box<BandwidthGossipPayload>()
        await svc.setOnGossipReceived { box.set($0) }
        sent.sourceUhid = "aether:peer:09"
        let ok = await svc.handle(sent)
        XCTAssertTrue(ok)

        let got = try XCTUnwrap(box.get())
        XCTAssertEqual(got.btlBwBps, 5_000_000)
        XCTAssertEqual(got.rtPropUs, 25_000)
        XCTAssertEqual(got.confidence, .medium)
        XCTAssertEqual(got.peerUhid, "aether:peer:09")
    }

    /// handle() on a BandwidthProbe raises onProbeReceived carrying the packet source.
    func testHandle_Probe_RaisesProbeReceived_WithSource() async throws {
        let svc = BandwidthWireService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let box = Box<BandwidthProbeReceived>()
        await svc.setOnProbeReceived { box.set($0) }

        let pkt = MeshPacket(
            type: .bandwidthProbe,
            sourceUhid: "aether:x:01",
            payload: BandwidthWireCodec.serializeProbe(BandwidthProbe(sequence: 9, senderSendUs: 123))
        )
        let ok = await svc.handle(pkt)
        XCTAssertTrue(ok)

        let got = try XCTUnwrap(box.get())
        XCTAssertEqual(got.probe.sequence, 9)
        XCTAssertEqual(got.fromUhid, "aether:x:01")
    }

    /// handle() on a BandwidthAck raises onAckReceived.
    func testHandle_Ack_RaisesAckReceived() async throws {
        let svc = BandwidthWireService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let box = Box<BandwidthProbeAck>()
        await svc.setOnAckReceived { box.set($0) }

        let pkt = MeshPacket(
            type: .bandwidthAck,
            sourceUhid: "aether:x:01",
            payload: BandwidthWireCodec.serializeAck(BandwidthProbeAck(
                sequence: 3, senderSendUs: 10, receiverReceiveUs: 20,
                receiverSendUs: 30, senderReceiveUs: 0, probeBytes: 64
            ))
        )
        let ok = await svc.handle(pkt)
        XCTAssertTrue(ok)

        let got = try XCTUnwrap(box.get())
        XCTAssertEqual(got.sequence, 3)
        XCTAssertEqual(got.probeBytes, 64)
    }

    /// A packet whose type is not a bandwidth type is rejected (returns false).
    func testHandle_WrongType_ReturnsFalse() async {
        let svc = BandwidthWireService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let ok = await svc.handle(MeshPacket(type: .data, payload: Data()))
        XCTAssertFalse(ok)
    }

    /// A bandwidth-typed packet with a too-short body is rejected (returns false).
    func testHandle_ShortPayload_ReturnsFalse() async {
        let svc = BandwidthWireService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let ok = await svc.handle(MeshPacket(type: .bandwidthProbe, sourceUhid: "aether:x:01", payload: Data([0x01, 0x02])))
        XCTAssertFalse(ok)
    }
}
