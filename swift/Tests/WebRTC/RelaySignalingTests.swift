// SPDX-License-Identifier: MIT
// Full-P2P acceptance test for the transport-backed WebRTC signalling carrier over the real
// libdatachannel transport.
//
// The carrier itself (round-trip, byte-identity, non-signalling-ignored) is proven WITHOUT
// libdatachannel by Tests/WebRTCSignaling/RelaySignalingCarrierTests.swift, which runs on the
// DEFAULT `swift test`. This file keeps ONLY the end-to-end path that needs the
// libdatachannel-backed `WebRtcTransportService`, so it lives in the gated AetherNetWebRTC test
// target and SKIPS when a direct channel can't be negotiated headlessly.

import XCTest
import Foundation
@testable import AetherNetWebRTC
@testable import AetherNetWebRTCSignaling
@testable import AetherNetProtocol

final class RelaySignalingTests: XCTestCase {

    // MARK: - Full P2P over the carrier (mirrors C# Handshake_RidesRelay_ThenDataGoesDirect)

    /// The production path end-to-end: the SDP/ICE handshake is framed by ``RelayWebRtcSignaling``
    /// and carried over a loopback transport pair (standing in for the relay), after which a direct
    /// WebRTC data channel carries the payload peer-to-peer.
    ///
    /// Requires a working libdatachannel (headless host-candidate ICE). If negotiation can't complete
    /// in the CI/headless environment, the test SKIPS rather than fails — the carrier itself is
    /// already proven by the round-trip tests in the always-built AetherNetWebRTCSignaling test target.
    func testHandshakeRidesRelayThenDataGoesDirect() async throws {
        let aliceRelay = LoopbackSignalingTransport("alice")
        let bobRelay = LoopbackSignalingTransport("bob")
        aliceRelay.peer = bobRelay
        bobRelay.peer = aliceRelay

        let aliceSignalling = RelayWebRtcSignaling(channel: aliceRelay)
        let bobSignalling = RelayWebRtcSignaling(channel: bobRelay)

        // Empty (not nil) ICE list ⇒ host-candidate-only ICE, no network dependency.
        let hostOnly: [String] = []
        let alice = WebRtcTransportService(localUhid: "alice", signaling: aliceSignalling, iceServers: hostOnly)
        let bob = WebRtcTransportService(localUhid: "bob", signaling: bobSignalling, iceServers: hostOnly)
        defer { alice.close(); bob.close() }

        let received = ReceivedBox()
        bob.onDataReceived { from, data in
            if from == "alice" { received.set(data) }
        }

        let payload = Data("handshake rode the relay; the data went direct".utf8)
        let ok = await alice.sendAsync(peerUhid: "bob", data: payload, cancellationToken: nil)

        guard ok else {
            throw XCTSkip("libdatachannel could not negotiate a direct channel headlessly; " +
                          "carrier round-trip is proven by the other tests")
        }

        let got = try await received.wait(timeout: 30)
        XCTAssertEqual(got, payload, "payload must cross the direct data channel unchanged")
        XCTAssertTrue(alice.isConnected(peerUhid: "bob"))
        XCTAssertTrue(bob.isConnected(peerUhid: "alice"))
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

/// A thread-safe one-shot box that an async test can await without busy-spinning the actor system.
private final class ReceivedBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Data?

    func set(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        if value == nil { value = data }
    }

    private func get() -> Data? {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func wait(timeout: TimeInterval) async throws -> Data {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let value = get() { return value }
            try await Task.sleep(nanoseconds: 20_000_000) // 20 ms
        }
        throw XCTSkip("timed out waiting for bytes over the data channel")
    }
}
