// SPDX-License-Identifier: MIT
// Acceptance tests for the transport-backed WebRTC signalling carrier (RelayWebRtcSignaling).
//
// These prove the carrier WITHOUT libdatachannel: they import only `AetherNetWebRTCSignaling`
// (Foundation-only), so they build and run on the DEFAULT `swift test` — no AETHERNET_WITH_WEBRTC,
// no native lib — exactly as the C SDK's transport-agnostic carrier test runs on every build.
//
// Two SEPARATE carrier instances (two nodes) exchange the SDP/ICE handshake over an in-process
// transport PAIR — the Swift analogue of the C# `RelaySignalingTests` over `LoopbackTransport`.
// The carrier frames each signal as `AWS1` ++ JSON, byte-identical to C#, so the handshake is
// cross-language. Signalling is out-of-band: none of this touches the mesh wire serialisation.
//
// The full offer/answer -> direct-data-flows test needs the libdatachannel-backed
// WebRtcTransportService, so it stays gated in Tests/WebRTC/RelaySignalingTests.swift.

import XCTest
import Foundation
@testable import AetherNetWebRTCSignaling

final class RelaySignalingCarrierTests: XCTestCase {

    // MARK: - Carrier round-trip (offer AND answer) — no libdatachannel needed

    /// Two carriers over a loopback pair must round-trip BOTH an offer and an answer, in order,
    /// proving the transport-backed signalling carrier itself (independent of libdatachannel).
    func testTwoCarriersRoundTripOfferAndAnswer() async throws {
        // Two "relay" endpoints wired to each other — the only thing the peers share.
        let aliceRelay = LoopbackSignalingTransport("alice")
        let bobRelay = LoopbackSignalingTransport("bob")
        aliceRelay.peer = bobRelay
        bobRelay.peer = aliceRelay

        let aliceSignalling = RelayWebRtcSignaling(channel: aliceRelay)
        let bobSignalling = RelayWebRtcSignaling(channel: bobRelay)

        // Bob collects what he receives; alice collects the answer bounced back.
        let bobInbox = SignalInbox()
        let aliceInbox = SignalInbox()
        await bobSignalling.onSignal { bobInbox.add($0) }
        await aliceSignalling.onSignal { aliceInbox.add($0) }

        // Alice → Bob: an offer.
        let offer = WebRtcSignal(
            fromUhid: "alice", toUhid: "bob", type: .offer, sdp: "v=0\r\no=- 1 1 IN IP4 0.0.0.0")
        let sentOffer = await aliceSignalling.send(peerUhid: "bob", signal: offer)
        XCTAssertTrue(sentOffer, "offer should be handed to the relay")

        let gotOffer = try await bobInbox.wait(count: 1, timeout: 5).first!
        XCTAssertEqual(gotOffer, offer, "the offer must survive the AWS1+JSON round-trip unchanged")

        // Bob to Alice: an answer (a full ICE-less handshake still exchanges offer + answer).
        let answer = WebRtcSignal(
            fromUhid: "bob", toUhid: "alice", type: .answer, sdp: "v=0\r\no=- 2 2 IN IP4 0.0.0.0")
        let sentAnswer = await bobSignalling.send(peerUhid: "alice", signal: answer)
        XCTAssertTrue(sentAnswer, "answer should be handed to the relay")

        let gotAnswer = try await aliceInbox.wait(count: 1, timeout: 5).first!
        XCTAssertEqual(gotAnswer, answer, "the answer must survive the round-trip unchanged")
        XCTAssertEqual(gotAnswer.type, .answer)
    }

    /// An ICE candidate (the third signal kind) must also survive the carrier, exercising the
    /// candidate / SdpMLineIndex / SdpMid members of the frame.
    func testCarrierRoundTripsIceCandidate() async throws {
        let aRelay = LoopbackSignalingTransport("a")
        let bRelay = LoopbackSignalingTransport("b")
        aRelay.peer = bRelay; bRelay.peer = aRelay

        let aSig = RelayWebRtcSignaling(channel: aRelay)
        let bSig = RelayWebRtcSignaling(channel: bRelay)

        let inbox = SignalInbox()
        await bSig.onSignal { inbox.add($0) }

        let cand = WebRtcSignal(
            fromUhid: "a", toUhid: "b", type: .iceCandidate,
            candidate: "candidate:1 1 UDP 2130706431 192.0.2.1 54321 typ host",
            sdpMLineIndex: 0, sdpMid: "0")
        let sentCand = await aSig.send(peerUhid: "b", signal: cand)
        XCTAssertTrue(sentCand, "candidate should be handed to the relay")

        let got = try await inbox.wait(count: 1, timeout: 5).first!
        XCTAssertEqual(got, cand, "the ICE candidate must survive the round-trip unchanged")
    }

    // MARK: - Non-signalling bytes are ignored (mirrors C# NonSignallingBytes_AreIgnored)

    /// App traffic without the `AWS1` prefix must not surface as a signal.
    func testNonSignallingBytesAreIgnored() async throws {
        let relay = LoopbackSignalingTransport("self")
        let peer = LoopbackSignalingTransport("peer")
        relay.peer = peer; peer.peer = relay

        let signalling = RelayWebRtcSignaling(channel: relay)
        let inbox = SignalInbox()
        await signalling.onSignal { inbox.add($0) }

        // Drive plain bytes into `relay` by sending from its peer.
        let delivered = await peer.sendAsync(peerUhid: "self", data: Data("ordinary app data".utf8))
        XCTAssertTrue(delivered)

        // Also send bytes whose first 3 chars collide with the prefix but the 4th differs.
        _ = await peer.sendAsync(peerUhid: "self", data: Data("AWS2 nope".utf8))

        let raised = await inbox.settle(after: 0.2)
        XCTAssertEqual(raised, 0, "non-prefixed app bytes must not be decoded as signalling")
    }

    // MARK: - Cross-language byte-identity of the framed body

    /// The framed bytes for an offer must equal the exact frame C# `RelayWebRtcSignaling` emits:
    /// `AWS1` ++ System.Text.Json of `WebRtcSignal` (PascalCase keys in declaration order, integer
    /// `Type`, nulls omitted, `SdpMLineIndex` always present). This is the cross-language contract.
    func testFramedOfferIsByteIdenticalToCSharp() {
        let offer = WebRtcSignal(fromUhid: "alice", toUhid: "bob", type: .offer, sdp: "v=0 fake-offer")
        let expectedJson = #"{"FromUhid":"alice","ToUhid":"bob","Type":0,"Sdp":"v=0 fake-offer","SdpMLineIndex":0}"#
        let expected = Data("AWS1".utf8) + Data(expectedJson.utf8)

        let frame = RelayWebRtcSignaling.frame(offer)
        XCTAssertEqual(frame, expected, "offer frame must be byte-identical to the C# carrier")

        // And it must decode back to the same signal (round-trip through the wire form).
        let body = frame.subdata(in: 4 ..< frame.count)
        XCTAssertEqual(WebRtcSignal.fromRelayJson(body), offer)
    }

    /// An ICE-candidate frame: `Sdp` is omitted, `Candidate` present before `SdpMLineIndex`, `SdpMid`
    /// last — the exact C# member order for that shape.
    func testFramedIceCandidateIsByteIdenticalToCSharp() {
        let cand = WebRtcSignal(
            fromUhid: "n1", toUhid: "n2", type: .iceCandidate,
            candidate: "candidate:1 1 UDP 2130706431 192.0.2.1 5000 typ host",
            sdpMLineIndex: 0, sdpMid: "0")
        let expectedJson = #"{"FromUhid":"n1","ToUhid":"n2","Type":2,"Candidate":"candidate:1 1 UDP 2130706431 192.0.2.1 5000 typ host","SdpMLineIndex":0,"SdpMid":"0"}"#
        let expected = Data("AWS1".utf8) + Data(expectedJson.utf8)

        XCTAssertEqual(RelayWebRtcSignaling.frame(cand), expected,
                       "ICE-candidate frame must be byte-identical to the C# carrier")
    }

    // MARK: - STJ-exact exotic-char escaping (proves byte-identity with System.Text.Json)
    //
    // The expected JSON below is the EXACT output of the C# `System.Text.Json` default
    // `JavaScriptEncoder.Default` (verified byte-for-byte, and matched by the TypeScript
    // `serializeSignalBody`/`stjString` reference). STJ escapes `+ < > & ' \`` and every non-ASCII
    // code point as UPPERCASE `\uXXXX`, while leaving `/` and `=` literal — so a real base64 SDP
    // fingerprint carrying `+` survives cross-language. The input Swift string literals hold the RAW
    // exotic/non-ASCII characters; the carrier's escaper must turn them into these `\uXXXX` escapes.

    /// An offer whose SDP carries the exotic ASCII STJ escapes but `JSON.stringify`/naive encoders do
    /// not: `+`→`+`, `<`→`<`, `>`→`>`, `&`→`&`. Base64 `/` and `=` stay literal.
    func testFramedOfferEscapesExoticAsciiLikeSTJ() {
        let offer = WebRtcSignal(
            fromUhid: "a", toUhid: "b", type: .offer,
            sdp: "a=fingerprint:sha-256 AB+/CD=xy <t> &z ual/set+ice")
        // Each `\\uXXXX` below is TWO wire chars: a literal backslash then `uXXXX` — the STJ escape as
        // it appears in the JSON bytes. `\\\\` never occurs here; `/` and `=` stay literal (STJ leaves
        // them). Written as a normal (non-raw) Swift string so `\\u` = backslash + `u`, not a Swift
        // scalar escape.
        let expectedJson = "{\"FromUhid\":\"a\",\"ToUhid\":\"b\",\"Type\":0,\"Sdp\":\"a=fingerprint:sha-256 AB\\u002B/CD=xy \\u003Ct\\u003E \\u0026z ual/set\\u002Bice\",\"SdpMLineIndex\":0}"
        let expected = Data("AWS1".utf8) + Data(expectedJson.utf8)

        XCTAssertEqual(RelayWebRtcSignaling.frame(offer), expected,
                       "offer frame must escape + < > & as uppercase \\uXXXX, byte-identical to C# STJ")
    }

    /// A candidate mixing exotic ASCII (`+ < > &`) with non-ASCII (`ç é 世`) in both `Candidate` and
    /// `SdpMid`. Non-ASCII becomes uppercase `\uXXXX` — `ç`→`ç`, `é`→`é`, `世`→`世` —
    /// exercising the UTF-16 escape path, while `/` stays literal. Byte-identical to the C#/TS reference.
    func testFramedIceCandidateEscapesNonAsciiLikeSTJ() {
        let cand = WebRtcSignal(
            fromUhid: "u", toUhid: "v", type: .iceCandidate,
            candidate: "a+b/c=d<e>f&g:h ç é 世",
            sdpMLineIndex: 3, sdpMid: "m/i+d")
        // Exotic ASCII → `\\u002B`/`\\u003C`/`\\u003E`/`\\u0026`; non-ASCII → `\\u00E7` (ç), `\\u00E9`
        // (é), `\\u4E16` (世). `/` stays literal. Normal Swift string: `\\u` = backslash + `u`.
        let expectedJson = "{\"FromUhid\":\"u\",\"ToUhid\":\"v\",\"Type\":2,\"Candidate\":\"a\\u002Bb/c=d\\u003Ce\\u003Ef\\u0026g:h \\u00E7 \\u00E9 \\u4E16\",\"SdpMLineIndex\":3,\"SdpMid\":\"m/i\\u002Bd\"}"
        let expected = Data("AWS1".utf8) + Data(expectedJson.utf8)

        XCTAssertEqual(RelayWebRtcSignaling.frame(cand), expected,
                       "candidate frame must escape non-ASCII as uppercase \\uXXXX, byte-identical to C# STJ")
    }
}

// MARK: - Test doubles

/// Minimal in-process ``SignalingTransport`` that delivers everything it sends to its paired
/// instance — a stand-in for the circuit/QUIC relay so the carrier can be exercised over a real
/// transport seam without a network. The Swift analogue of the C# `LoopbackTransport`.
private final class LoopbackSignalingTransport: SignalingTransport, @unchecked Sendable {
    private let localUhid: String
    private let lock = NSLock()
    private var handler: (@Sendable (String, Data) -> Void)?

    /// The far end. Set once on both instances to wire the pair together.
    weak var peer: LoopbackSignalingTransport?

    init(_ localUhid: String) { self.localUhid = localUhid }

    @discardableResult
    func sendAsync(peerUhid: String, data: Data) async -> Bool {
        guard let peer else { return false }
        peer.receive(from: localUhid, data: data) // ordered, reliable delivery to the far end
        return true
    }

    func onDataReceived(_ handler: @escaping @Sendable (String, Data) -> Void) {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
    }

    private func receive(from fromUhid: String, data: Data) {
        lock.lock()
        let h = handler
        lock.unlock()
        h?(fromUhid, data)
    }
}

/// Thread-safe collector of received signals, awaitable without busy-spinning the actor system.
private final class SignalInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var signals: [WebRtcSignal] = []

    func add(_ signal: WebRtcSignal) {
        lock.lock(); defer { lock.unlock() }
        signals.append(signal)
    }

    private func snapshot() -> [WebRtcSignal] {
        lock.lock(); defer { lock.unlock() }
        return signals
    }

    /// Polls until at least `count` signals arrive or `timeout` elapses; throws on timeout.
    func wait(count: Int, timeout: TimeInterval) async throws -> [WebRtcSignal] {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let s = snapshot()
            if s.count >= count { return s }
            try await Task.sleep(nanoseconds: 10_000_000) // 10 ms
        }
        throw XCTSkip("timed out waiting for \(count) signal(s) over the carrier")
    }

    /// Waits `seconds`, then returns how many signals landed (for negative assertions).
    func settle(after seconds: TimeInterval) async -> Int {
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        return snapshot().count
    }
}
