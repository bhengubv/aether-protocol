// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Unit tests for ``PreKeyExchangeService`` (PacketType.preKeyRequest 25 / preKeyResponse 26).
///
/// Two halves, mirroring the C# `PreKeyExchangeTests` case-for-case:
///   1. Byte-identity — the hand-built wire encoders must produce EXACTLY the UTF-8 bytes in
///      `fixtures/prekey/vectors.json` (snake_case, field order request_id/requester_uhid for the
///      request and request_id/uhid/identity_key/identity_key_x25519/pre_key_id/pre_key/
///      signed_pre_key_id/signed_pre_key/signed_pre_key_signature for the response, no whitespace,
///      lowercase-dashed UUID, bare-integer ids, STANDARD base64 byte fields). This is the
///      cross-language gate.
///   2. Behaviour — directed request/response transport of a ``PreKeyBundle``: a fake ``MeshSender``
///      captures directed sends; requestBundle mints an id + sends a request; a request with a local
///      bundle set replies with a response to the requester; a request with no local bundle set is
///      dropped with no send; a response caches the bundle + fires onBundleReceived; a wrong packet
///      type is rejected.
final class PreKeyExchangeTests: XCTestCase {

    // MARK: - Fakes / capture

    // Directed sends are captured by the shared `FakeMeshSender` (Tests/FakeMeshSender.swift),
    // a `final class` whose `unicasts()` returns the recorded `UnicastRecord`s (`.packet` /
    // `.nextHopUhid`). A per-test nested `actor` fake is deliberately avoided: an `actor`
    // conforming to the nonisolated `MeshSender` requirements trips Swift 6 `#ConformanceIsolation`.

    /// Thread-safe one-shot capture box for the `@Sendable` bundle-received callback.
    private final class EventBox: @unchecked Sendable {
        private let lock = NSLock()
        private var value: PreKeyBundleReceived?
        func set(_ e: PreKeyBundleReceived) { lock.lock(); value = e; lock.unlock() }
        func get() -> PreKeyBundleReceived? { lock.lock(); defer { lock.unlock() }; return value }
    }

    /// The fixed constant-byte-fill sample bundle mirroring the C# test's `SampleBundle`
    /// (0x11 identity, 0x22 identity_x25519, 0x33 pre_key, 0x44 signed_pre_key, 0x55 signature)
    /// so a field swap is caught.
    private func sampleBundle(uhid: String = "aether:bob:02") -> PreKeyBundle {
        PreKeyBundle(
            uhid: uhid,
            identityKey: Data(repeating: 0x11, count: 32),
            identityKeyX25519: Data(repeating: 0x22, count: 32),
            preKeyId: 4242,
            preKey: Data(repeating: 0x33, count: 32),
            signedPreKeyId: 77,
            signedPreKey: Data(repeating: 0x44, count: 32),
            signedPreKeySignature: Data(repeating: 0x55, count: 64)
        )
    }

    // MARK: - Byte-identity corpus

    private struct Vector: Decodable {
        let name: String
        let kind: String
        let request_id: String
        // request-only
        let requester_uhid: String?
        // response-only
        let uhid: String?
        let pre_key_id: Int32?
        let signed_pre_key_id: Int32?
        let identity_key: String?
        let identity_key_x25519: String?
        let pre_key: String?
        let signed_pre_key: String?
        let signed_pre_key_signature: String?
        let expected_json: String
    }

    private struct Corpus: Decodable {
        let description: String
        let vectors: [Vector]
    }

    /// Locate `fixtures/prekey/vectors.json` by walking up from this source file's directory
    /// (`#file`) to the repo root — independent of CWD, the same parent-traversal idiom the URI,
    /// Bandwidth, and VideoCallControl fixture drivers use.
    private func loadCorpus() throws -> Corpus {
        var url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = url
                .appendingPathComponent("fixtures")
                .appendingPathComponent("prekey")
                .appendingPathComponent("vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(Corpus.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/prekey/vectors.json walking up from \(#file)")
        throw CocoaError(.fileNoSuchFile)
    }

    /// Every fixture vector must hand-encode to EXACTLY its `expected_json` bytes. Byte fields are
    /// decoded from the vector's base64 back to raw so the encoder round-trips through real key
    /// material, not just the sample fill.
    func testWire_MatchesCanonicalVectors() throws {
        let corpus = try loadCorpus()
        XCTAssertFalse(corpus.vectors.isEmpty, "corpus has no vectors")

        for v in corpus.vectors {
            let requestId = try XCTUnwrap(UUID(uuidString: v.request_id), "[\(v.name)] bad UUID \(v.request_id)")
            let bytes: Data
            switch v.kind {
            case "request":
                bytes = _preKeyRequestWireBytesForTests(
                    requestId: requestId,
                    requesterUhid: try XCTUnwrap(v.requester_uhid, "[\(v.name)] missing requester_uhid")
                )
            case "response":
                let bundle = PreKeyBundle(
                    uhid: try XCTUnwrap(v.uhid),
                    identityKey: try b64(v.identity_key, v.name, "identity_key"),
                    identityKeyX25519: try b64(v.identity_key_x25519, v.name, "identity_key_x25519"),
                    preKeyId: try XCTUnwrap(v.pre_key_id),
                    preKey: try b64(v.pre_key, v.name, "pre_key"),
                    signedPreKeyId: try XCTUnwrap(v.signed_pre_key_id),
                    signedPreKey: try b64(v.signed_pre_key, v.name, "signed_pre_key"),
                    signedPreKeySignature: try b64(v.signed_pre_key_signature, v.name, "signed_pre_key_signature")
                )
                bytes = _preKeyResponseWireBytesForTests(requestId: requestId, bundle: bundle)
            default:
                XCTFail("[\(v.name)] unknown kind \(v.kind)")
                continue
            }
            let got = String(decoding: bytes, as: UTF8.self)
            XCTAssertEqual(got, v.expected_json, "[\(v.name)] wire byte mismatch")
        }
    }

    /// The two InlineData cases from the C# byte-identity tests, asserted directly (belt-and-braces
    /// alongside the fixture-driven test, so byte-identity is pinned even if the fixture file moves).
    func testWire_MatchesInlineCanonicalCases() throws {
        let request = _preKeyRequestWireBytesForTests(
            requestId: try XCTUnwrap(UUID(uuidString: "11112222-3333-4444-5555-666677778888")),
            requesterUhid: "aether:alice:01"
        )
        XCTAssertEqual(
            String(decoding: request, as: UTF8.self),
            "{\"request_id\":\"11112222-3333-4444-5555-666677778888\",\"requester_uhid\":\"aether:alice:01\"}"
        )

        let response = _preKeyResponseWireBytesForTests(
            requestId: try XCTUnwrap(UUID(uuidString: "7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a")),
            bundle: sampleBundle()
        )
        XCTAssertEqual(
            String(decoding: response, as: UTF8.self),
            "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\","
            + "\"identity_key\":\"ERERERERERERERERERERERERERERERERERERERERERE=\","
            + "\"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\","
            + "\"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\","
            + "\"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\","
            + "\"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}"
        )
    }

    /// A response payload round-trips back into an identical ``PreKeyBundle`` (mirrors C#
    /// `ResponsePayload_RoundTripsThroughBundle`).
    func testResponse_RoundTripsThroughBundle() throws {
        let original = sampleBundle()
        let wire = _preKeyResponseWireBytesForTests(requestId: UUID(), bundle: original)
        let back = try XCTUnwrap(JSONDecoder().decodePreKeyResponse(wire))
        XCTAssertEqual(back.bundle.uhid, original.uhid)
        XCTAssertEqual(back.bundle.preKeyId, original.preKeyId)
        XCTAssertEqual(back.bundle.signedPreKeyId, original.signedPreKeyId)
        XCTAssertEqual(back.bundle.identityKey, original.identityKey)
        XCTAssertEqual(back.bundle.signedPreKeySignature, original.signedPreKeySignature)
    }

    // MARK: - Behaviour

    /// requestBundle mints a non-empty request id and directed-sends a preKeyRequest to the peer,
    /// carrying our UHID as requester_uhid (mirrors C# `Request_SendsDirectedPreKeyRequest_AndReturnsId`).
    func testRequest_SendsDirectedPreKeyRequest_AndReturnsId() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = PreKeyExchangeService(sender: sender)

        let reqId = await svc.requestBundle("aether:bob:02")

        XCTAssertNotEqual(reqId, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        let sent = try XCTUnwrap(sends.first)
        XCTAssertEqual(sent.packet.type, .preKeyRequest)
        XCTAssertEqual(sent.nextHopUhid, "aether:bob:02")

        let body = try XCTUnwrap(JSONDecoder().decodePreKeyRequest(sent.packet.payload))
        XCTAssertEqual(body.requestId, reqId)
        XCTAssertEqual(body.requesterUhid, "aether:alice:01")
    }

    /// A preKeyRequest with a local bundle set replies with a directed preKeyResponse to the
    /// requester, echoing the request id (mirrors C#
    /// `HandleRequest_WithLocalBundle_SendsDirectedResponseToRequester`).
    func testHandleRequest_WithLocalBundle_SendsDirectedResponseToRequester() async throws {
        let sender = FakeMeshSender(localUhid: "aether:bob:02")
        let svc = PreKeyExchangeService(sender: sender)
        await svc.setLocalBundle(sampleBundle(uhid: "aether:bob:02"))

        let reqId = UUID()
        let reqPkt = MeshPacket(
            type: .preKeyRequest,
            sourceUhid: "aether:alice:01",
            destinationUhid: "aether:bob:02",
            payload: _preKeyRequestWireBytesForTests(requestId: reqId, requesterUhid: "aether:alice:01")
        )

        let ok = await svc.handle(reqPkt)
        XCTAssertTrue(ok)
        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        let sent = try XCTUnwrap(sends.first)
        XCTAssertEqual(sent.packet.type, .preKeyResponse)
        XCTAssertEqual(sent.nextHopUhid, "aether:alice:01")

        let body = try XCTUnwrap(JSONDecoder().decodePreKeyResponse(sent.packet.payload))
        XCTAssertEqual(body.requestId, reqId)
        XCTAssertEqual(body.bundle.uhid, "aether:bob:02")
        XCTAssertEqual(body.bundle.preKeyId, 4242)
        XCTAssertEqual(body.bundle.signedPreKeySignature.count, 64)
    }

    /// A preKeyRequest with NO local bundle set is dropped: returns false, sends nothing (mirrors C#
    /// `HandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing`).
    func testHandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing() async throws {
        let sender = FakeMeshSender(localUhid: "aether:local:01")
        let svc = PreKeyExchangeService(sender: sender)
        let reqPkt = MeshPacket(
            type: .preKeyRequest,
            sourceUhid: "aether:alice:01",
            payload: _preKeyRequestWireBytesForTests(requestId: UUID(), requesterUhid: "aether:alice:01")
        )

        let ok = await svc.handle(reqPkt)
        XCTAssertFalse(ok)
        XCTAssertTrue(sender.unicasts().isEmpty)
    }

    /// A preKeyResponse caches the bundle by uhid and fires onBundleReceived (mirrors C#
    /// `HandleResponse_CachesBundle_AndRaisesEvent`).
    func testHandleResponse_CachesBundle_AndFiresCallback() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = PreKeyExchangeService(sender: sender)
        let box = EventBox()
        await svc.setOnBundleReceived { box.set($0) }

        let reqId = UUID()
        let respPkt = MeshPacket(
            type: .preKeyResponse,
            sourceUhid: "aether:bob:02",
            destinationUhid: "aether:alice:01",
            payload: _preKeyResponseWireBytesForTests(requestId: reqId, bundle: sampleBundle(uhid: "aether:bob:02"))
        )

        let ok = await svc.handle(respPkt)
        XCTAssertTrue(ok)

        let got = try XCTUnwrap(box.get())
        XCTAssertEqual(got.requestId, reqId)
        XCTAssertEqual(got.fromUhid, "aether:bob:02")
        XCTAssertEqual(got.bundle.uhid, "aether:bob:02")

        let received = await svc.getReceivedBundle("aether:bob:02")
        let cached = try XCTUnwrap(received)
        XCTAssertEqual(cached.preKeyId, 4242)
    }

    /// A packet whose type is neither preKeyRequest nor preKeyResponse is rejected (returns false;
    /// mirrors C# `Handle_WrongPacketType_ReturnsFalse`).
    func testHandle_WrongPacketType_ReturnsFalse() async {
        let svc = PreKeyExchangeService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let pkt = MeshPacket(type: .data, sourceUhid: "aether:x:01", payload: Data())
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    // MARK: - Helpers

    private func b64(_ s: String?, _ name: String, _ field: String) throws -> Data {
        let str = try XCTUnwrap(s, "[\(name)] missing \(field)")
        return try XCTUnwrap(Data(base64Encoded: str), "[\(name)] bad base64 in \(field)")
    }
}

// Decodes the on-wire PreKey payloads for test assertions (order-independent parse). Byte fields
// decode from standard base64 under JSONDecoder's default `.base64` data strategy.
private struct _TestPreKeyRequestWire: Decodable {
    let request_id: UUID
    let requester_uhid: String
}

private struct _TestPreKeyResponseWire: Decodable {
    let request_id: UUID
    let uhid: String
    let identity_key: Data
    let identity_key_x25519: Data
    let pre_key_id: Int32
    let pre_key: Data
    let signed_pre_key_id: Int32
    let signed_pre_key: Data
    let signed_pre_key_signature: Data
}

private extension JSONDecoder {
    func decodePreKeyRequest(_ data: Data) -> (requestId: UUID, requesterUhid: String)? {
        guard let w = try? decode(_TestPreKeyRequestWire.self, from: data) else { return nil }
        return (w.request_id, w.requester_uhid)
    }

    func decodePreKeyResponse(_ data: Data) -> (requestId: UUID, bundle: PreKeyBundle)? {
        guard let w = try? decode(_TestPreKeyResponseWire.self, from: data) else { return nil }
        let bundle = PreKeyBundle(
            uhid: w.uhid,
            identityKey: w.identity_key,
            identityKeyX25519: w.identity_key_x25519,
            preKeyId: w.pre_key_id,
            preKey: w.pre_key,
            signedPreKeyId: w.signed_pre_key_id,
            signedPreKey: w.signed_pre_key,
            signedPreKeySignature: w.signed_pre_key_signature
        )
        return (w.request_id, bundle)
    }
}
